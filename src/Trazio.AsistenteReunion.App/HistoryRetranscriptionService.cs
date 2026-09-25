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

    public async Task<TranscriptModelRevision> RunSelectedSegmentAsync(
        SessionSummary session,
        TranscriptSegment originalSegment,
        string modelPath,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalSegment);
        if (session.State is not (SessionState.Completed or SessionState.Interrupted))
            throw new InvalidOperationException("Detén la sesión activa antes de retranscribirla.");
        if (originalSegment.SessionId != session.Id ||
            originalSegment.Start < TimeSpan.Zero ||
            originalSegment.End <= originalSegment.Start ||
            originalSegment.End - originalSegment.Start > TimeSpan.FromSeconds(60))
            throw new InvalidOperationException("El fragmento original debe durar hasta 60 segundos.");
        var source = originalSegment.Source;
        var key = $"{session.Id}:{source}";
        if (!_running.TryAdd(key, 0))
            throw new InvalidOperationException("Ya hay una retranscripción en curso para esta sesión y fuente.");

        byte[]? pcm = null;
        TranscriptModelRevision? revision = null;
        ITranscriptionTransport? transport = null;
        ITranscriptionExecutionAuthorization? authorization = null;
        var timer = Stopwatch.StartNew();
        try
        {
            var chunks = await store.GetArchivedAudioAsync(session.Id, source, cancellationToken);
            if (chunks.Count == 0)
                throw new InvalidOperationException("No hay audio conservado para el fragmento seleccionado.");
            if (transportFactory is ISelectedSegmentPreauthorizationFactory preauthorizationFactory)
                authorization = await preauthorizationFactory.AuthorizeAsync(modelPath, language, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            pcm = await RetainedAudioInterval.ReadExactAsync(
                archive, session, source, chunks,
                originalSegment.Start, originalSegment.End, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var scope = new TranscriptModelRevisionScope(
                originalSegment.Id, originalSegment.Start, originalSegment.End);
            revision = await store.StartQwenSegmentModelRevisionAsync(
                session.Id, source, scope, Path.GetFileName(modelPath),
                modelHash: null, language, CancellationToken.None);
            transport = authorization is null
                ? await transportFactory.StartAsync(modelPath, language, cancellationToken)
                : await authorization.StartAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(transport.VerifiedModelHash))
                throw new InvalidOperationException("El proceso de transcripción no pudo verificar la identidad del modelo.");
            await store.SetModelRevisionVerifiedModelHashAsync(
                revision.Id, transport.VerifiedModelHash, cancellationToken);
            var response = await transport.TranscribeAsync(
                $"{revision.Id}:segment:{originalSegment.Id}", pcm, cancellationToken);
            if (!response.Success)
                throw new InvalidOperationException(response.Error ?? "Falló el proceso de transcripción.");
            if (response.Segments is null || response.Segments.Count == 0)
                throw new InvalidOperationException("El modelo no devolvió texto para el fragmento seleccionado.");
            var elapsedMs = (long)(originalSegment.End - originalSegment.Start).TotalMilliseconds;
            long sequence = 0;
            foreach (var item in response.Segments)
            {
                if (item.StartMilliseconds < 0 || item.EndMilliseconds <= item.StartMilliseconds ||
                    item.EndMilliseconds > elapsedMs || string.IsNullOrWhiteSpace(item.Text))
                    throw new InvalidDataException("El modelo devolvió tiempos o texto fuera del fragmento seleccionado.");
                await store.SaveModelRevisionSegmentAsync(new(
                    Guid.NewGuid().ToString("N"), revision.Id, sequence++,
                    originalSegment.Start + TimeSpan.FromMilliseconds(item.StartMilliseconds),
                    originalSegment.Start + TimeSpan.FromMilliseconds(item.EndMilliseconds),
                    item.Text), cancellationToken);
            }
            timer.Stop();
            await store.FinishModelRevisionAsync(
                revision.Id, ModelRevisionStatus.Succeeded, DateTimeOffset.UtcNow,
                timer.Elapsed, null, CancellationToken.None);
            return (await store.ListModelRevisionsAsync(session.Id, source, false, CancellationToken.None))
                .Single(item => item.Id == revision.Id);
        }
        catch (OperationCanceledException)
        {
            timer.Stop();
            if (revision is not null)
                await store.FinishModelRevisionAsync(
                    revision.Id, ModelRevisionStatus.Cancelled, DateTimeOffset.UtcNow,
                    timer.Elapsed, "Cancelada por el usuario o por un cambio de selección.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            timer.Stop();
            if (revision is not null)
                await store.FinishModelRevisionAsync(
                    revision.Id, ModelRevisionStatus.Failed, DateTimeOffset.UtcNow,
                    timer.Elapsed, ex.Message, CancellationToken.None);
            throw;
        }
        finally
        {
            if (pcm is not null) CryptographicOperations.ZeroMemory(pcm);
            try { await DisposeTransportWithoutChangingRevisionOutcomeAsync(transport); }
            finally
            {
                try { if (authorization is not null) await authorization.DisposeAsync(); }
                finally { _running.TryRemove(key, out _); }
            }
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

public static class RetainedAudioInterval
{
    private const long SampleTicks = TimeSpan.TicksPerSecond / 16_000;
    private const long AlignmentToleranceTicks = SampleTicks / 2;

    public static async Task<byte[]> ReadExactAsync(
        AudioArchiveStore archive,
        SessionSummary session,
        AudioSourceKind source,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken)
    {
        if (start < TimeSpan.Zero || end <= start ||
            end - start > TimeSpan.FromSeconds(60) ||
            (end.Ticks - start.Ticks) % SampleTicks != 0)
            throw new InvalidOperationException("El intervalo seleccionado no se puede representar con audio PCM16.");
        if (chunks.Count == 0)
            throw new InvalidOperationException("No hay audio conservado para el fragmento seleccionado.");
        var expectedBytes = checked((int)((end.Ticks - start.Ticks) / SampleTicks * 2));
        using var output = new MemoryStream(expectedBytes);
        try
        {
            var cursor = start.Ticks;
            long? previousSequence = null;
            string? previousRunId = null;
            long? previousEpoch = null;
            long? expectedSourceSample = null;
            bool? previousHasMetadata = null;
            foreach (var chunk in chunks.OrderBy(item => item.StartedAt).ThenBy(item => item.Sequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cursor >= end.Ticks - AlignmentToleranceTicks) break;
                if (chunk.SessionId != session.Id || chunk.Source != source)
                    throw new InvalidDataException("El audio conservado no corresponde al fragmento seleccionado.");
                var hasMetadata = chunk.CaptureRunId is not null &&
                    chunk.ContinuityEpoch is not null && chunk.FirstSourceSample is not null;
                if (hasMetadata != (chunk.CaptureRunId is not null ||
                    chunk.ContinuityEpoch is not null || chunk.FirstSourceSample is not null) ||
                    hasMetadata && (string.IsNullOrWhiteSpace(chunk.CaptureRunId) ||
                                    chunk.ContinuityEpoch < 0 || chunk.FirstSourceSample < 0))
                    throw new InvalidDataException("Los metadatos de continuidad del audio no son válidos.");
                var chunkStart = (chunk.StartedAt - session.StartedAt).Ticks;
                if (chunkStart > end.Ticks) break;
                if (chunkStart + chunk.Duration.Ticks <= cursor - AlignmentToleranceTicks) continue;
                if (chunkStart < 0) throw new InvalidDataException("El audio conservado tiene tiempos inválidos.");
                // Duration is derived from PCM at archive write time; read and verify the encrypted WAV.
                var wav = await archive.ReadChunkAsync(chunk, cancellationToken);
                try
                {
                    var pcm = WavPcm.GetPcm16(wav);
                    if (pcm.Length % 2 != 0) throw new InvalidDataException("El audio PCM16 no está completo.");
                    var chunkDuration = checked((pcm.Length / 2L) * SampleTicks);
                    if (Math.Abs(chunk.Duration.Ticks - chunkDuration) >= TimeSpan.TicksPerMillisecond)
                        throw new InvalidDataException("El audio conservado no coincide con su duración registrada.");
                    var chunkEnd = checked(chunkStart + chunkDuration);
                    if (chunkEnd <= cursor - AlignmentToleranceTicks) continue;
                    if (previousSequence is not null &&
                        (chunk.Sequence != previousSequence.Value + 1 ||
                         previousHasMetadata != hasMetadata ||
                         hasMetadata && (chunk.CaptureRunId != previousRunId ||
                                         chunk.ContinuityEpoch != previousEpoch ||
                                         chunk.FirstSourceSample != expectedSourceSample)))
                        throw new InvalidOperationException("Falta audio continuo en el fragmento seleccionado.");
                    if (chunkStart > cursor + AlignmentToleranceTicks ||
                        previousSequence is not null && chunkStart < cursor - AlignmentToleranceTicks)
                        throw new InvalidOperationException("Falta audio continuo en el fragmento seleccionado.");
                    var from = Math.Max(cursor, chunkStart);
                    var to = Math.Min(end.Ticks, chunkEnd);
                    var firstSample = checked((int)Math.Round(
                        (from - chunkStart) / (double)SampleTicks, MidpointRounding.AwayFromZero));
                    var lastSample = checked((int)Math.Round(
                        (to - chunkStart) / (double)SampleTicks, MidpointRounding.AwayFromZero));
                    if (firstSample < 0 || lastSample > pcm.Length / 2 || lastSample <= firstSample)
                        throw new InvalidOperationException("El fragmento solicitado no está cubierto por audio completo.");
                    output.Write(pcm.Slice(firstSample * 2, (lastSample - firstSample) * 2).Span);
                    cursor = checked(chunkStart + (long)lastSample * SampleTicks);
                    previousSequence = chunk.Sequence;
                    previousHasMetadata = hasMetadata;
                    previousRunId = chunk.CaptureRunId;
                    previousEpoch = chunk.ContinuityEpoch;
                    expectedSourceSample = hasMetadata
                        ? checked(chunk.FirstSourceSample!.Value + pcm.Length / 2L) : null;
                }
                finally { CryptographicOperations.ZeroMemory(wav); }
            }
            if (cursor < end.Ticks - AlignmentToleranceTicks || output.Length != expectedBytes)
                throw new InvalidOperationException("Falta audio continuo en el fragmento seleccionado.");
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(output.GetBuffer()); }
    }
}
