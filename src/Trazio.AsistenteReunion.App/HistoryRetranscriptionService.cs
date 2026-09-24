using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed class HistoryRetranscriptionService(
    SqliteSessionStore store,
    AudioArchiveStore archive,
    ITranscriptionTransportFactory transportFactory)
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    public Task<TranscriptModelRevision> RunAsync(
        SessionSummary session,
        AudioSourceKind source,
        string modelPath,
        string language,
        CancellationToken cancellationToken = default) =>
        RunAsync(session, source, modelPath, language, GlossaryPromptPlan.None, cancellationToken);

    public async Task<TranscriptModelRevision> RunAsync(
        SessionSummary session,
        AudioSourceKind source,
        string modelPath,
        string language,
        GlossaryPromptPlan glossaryPromptPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(glossaryPromptPlan);
        if (session.State is not (SessionState.Completed or SessionState.Interrupted))
            throw new InvalidOperationException("Detén la sesión activa antes de retranscribirla.");
        var chunks = (await store.GetArchivedAudioAsync(session.Id, source, cancellationToken))
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (chunks.Length == 0)
            throw new InvalidOperationException("No hay audio conservado disponible para esta fuente. El audio eliminado no se puede recuperar.");
        RetainedAudioContinuity.Validate(chunks, session.StartedAt);

        var key = $"{session.Id}:{source}";
        if (!_running.TryAdd(key, 0))
            throw new InvalidOperationException("Ya hay una retranscripción en curso para esta sesión y fuente.");

        TranscriptModelRevision? revision = null;
        ITranscriptionTransport? transport = null;
        var timer = Stopwatch.StartNew();
        try
        {
            revision = await store.StartModelRevisionAsync(
                session.Id,
                source,
                Path.GetFileName(modelPath),
                modelHash: null,
                language,
                glossaryPromptPlan.Version,
                CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            transport = await transportFactory.StartAsync(modelPath, language, glossaryPromptPlan.Prompt, cancellationToken);
            if (string.IsNullOrWhiteSpace(transport.VerifiedModelHash))
                throw new InvalidOperationException("El proceso de transcripción no pudo verificar la identidad del modelo.");
            await store.SetModelRevisionVerifiedModelHashAsync(
                revision.Id,
                transport.VerifiedModelHash,
                cancellationToken);

            long revisionSequence = 0;
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wav = await archive.ReadChunkAsync(chunk, cancellationToken);
                try
                {
                    var pcm = WavPcm.GetPcm16(wav).ToArray();
                    try
                    {
                        var response = await transport.TranscribeAsync(
                            $"{revision.Id}:{chunk.Sequence}",
                            pcm,
                            cancellationToken);
                        if (!response.Success)
                            throw new InvalidOperationException(response.Error ?? "Falló el proceso de transcripción.");
                        var chunkOffset = chunk.StartedAt - session.StartedAt;
                        foreach (var segment in response.Segments ?? [])
                        {
                            var item = new TranscriptModelRevisionSegment(
                                Guid.NewGuid().ToString("N"),
                                revision.Id,
                                revisionSequence++,
                                chunkOffset + TimeSpan.FromMilliseconds(segment.StartMilliseconds),
                                chunkOffset + TimeSpan.FromMilliseconds(segment.EndMilliseconds),
                                segment.Text);
                            await store.SaveModelRevisionSegmentAsync(item, cancellationToken);
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(pcm); }
                }
                finally { CryptographicOperations.ZeroMemory(wav); }
            }

            timer.Stop();
            await store.FinishModelRevisionAsync(
                revision.Id,
                ModelRevisionStatus.Succeeded,
                DateTimeOffset.UtcNow,
                timer.Elapsed,
                error: null,
                CancellationToken.None);
            return (await store.ListModelRevisionsAsync(session.Id, source, false, CancellationToken.None))
                .Single(item => item.Id == revision.Id);
        }
        catch (OperationCanceledException)
        {
            timer.Stop();
            if (revision is not null)
                await store.FinishModelRevisionAsync(
                    revision.Id,
                    ModelRevisionStatus.Cancelled,
                    DateTimeOffset.UtcNow,
                    timer.Elapsed,
                    "Cancelada por el usuario o por un cambio de selección.",
                    CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            timer.Stop();
            if (revision is not null)
                await store.FinishModelRevisionAsync(
                    revision.Id,
                    ModelRevisionStatus.Failed,
                    DateTimeOffset.UtcNow,
                    timer.Elapsed,
                    ex.Message,
                    CancellationToken.None);
            throw;
        }
        finally
        {
            try { await DisposeTransportWithoutChangingRevisionOutcomeAsync(transport); }
            finally { _running.TryRemove(key, out _); }
        }
    }

    private static async Task DisposeTransportWithoutChangingRevisionOutcomeAsync(ITranscriptionTransport? transport)
    {
        if (transport is null) return;
        try { await transport.StopAsync(CancellationToken.None); }
        catch (Exception ex) { Debug.WriteLine($"No se pudo detener el transporte de transcripción: {ex.Message}"); }
        try { await transport.DisposeAsync(); }
        catch (Exception ex) { Debug.WriteLine($"No se pudo liberar el transporte de transcripción: {ex.Message}"); }
    }
}

public static class RetainedAudioContinuity
{
    public static void Validate(IReadOnlyList<ArchivedAudioChunk> chunks, DateTimeOffset sessionStartedAt)
    {
        if (chunks.Count == 0) throw new InvalidOperationException("No hay audio conservado disponible para esta fuente.");
        var ordered = chunks.OrderBy(item => item.Sequence).ToArray();
        if (ordered[0].Sequence != 0)
            throw new InvalidOperationException("El audio conservado comienza después del inicio original. No se puede retranscribir porque el audio anterior fue eliminado.");
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Sequence != index)
                throw new InvalidOperationException("Falta un fragmento del audio conservado. La retranscripción necesita una línea de tiempo completa de la fuente.");
            if (ordered[index].StartedAt < sessionStartedAt)
                throw new InvalidOperationException("Los tiempos del audio conservado no coinciden con la sesión guardada.");
        }
    }
}
