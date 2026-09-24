using System.Security.Cryptography;
using System.Text;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class GlossaryAssistedRetranscriptionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "trazio-glossary-retranscription-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private AudioArchiveStore _archive = null!;
    private string _model = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _store = new(Path.Combine(_root, "test.db"), _protector);
        await _store.InitializeAsync();
        _archive = new(Path.Combine(_root, "audio"), _store, _protector);
        _model = Path.Combine(_root, "model.bin");
        await File.WriteAllBytesAsync(_model, [1, 2, 3, 4]);
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_ConfirmedGlossaryPassesPromptAndPersistsEncryptedVersion()
    {
        var session = await CreateCompletedSessionWithAudioAsync();
        var plan = GlossaryPromptPlanner.Create(
        [
            new(
                "entry",
                "Trazio",
                "Trazzio",
                "Producto",
                true,
                GlossaryEntryOrigin.ImportedFile,
                null,
                "batch",
                DateTimeOffset.UtcNow)
        ]);
        var factory = new PromptCapturingTransportFactory();
        var service = new HistoryRetranscriptionService(_store, _archive, factory);

        var revision = await service.RunAsync(
            ToSummary(session),
            AudioSourceKind.Microphone,
            _model,
            "es",
            plan);

        Assert.Equal(plan.Prompt, factory.InitialPrompt);
        Assert.Equal(plan.Version, revision.GlossaryPromptVersion);
        Assert.Contains("diccionario confirmado", new HistoryRevisionItem(revision).Label, StringComparison.Ordinal);
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_root, "test.db")));
        Assert.DoesNotContain(plan.Version, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Prompt!, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithoutGlossaryPreservesLegacyContract()
    {
        var session = await CreateCompletedSessionWithAudioAsync();
        var factory = new PromptCapturingTransportFactory();
        var service = new HistoryRetranscriptionService(_store, _archive, factory);

        var revision = await service.RunAsync(
            ToSummary(session),
            AudioSourceKind.Microphone,
            _model,
            "es");

        Assert.Null(factory.InitialPrompt);
        Assert.Equal(GlossaryPromptPlan.NoGlossaryVersion, revision.GlossaryPromptVersion);
        Assert.DoesNotContain("diccionario confirmado", new HistoryRevisionItem(revision).Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PipeFraming_RoundTripsOptionalInitialPrompt()
    {
        await using var stream = new MemoryStream();
        var expected = new WorkerRequest(
            "start",
            "model.bin",
            "es",
            InitialPrompt: "Vocabulario confirmado para esta transcripción: Trazio.");

        await PipeFraming.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await PipeFraming.ReadAsync<WorkerRequest>(stream);

        Assert.Equal(expected.InitialPrompt, actual.InitialPrompt);
    }

    private async Task<MeetingSession> CreateCompletedSessionWithAudioAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new MeetingSession(
            Guid.NewGuid().ToString("N"),
            "Reunión",
            now,
            now.AddMinutes(1),
            SessionState.Completed);
        await _store.CreateSessionAsync(session);
        await using var writer = _archive.CreateSession(session.Id, long.MaxValue);
        await writer.AppendAsync(new(
            AudioSourceKind.Microphone,
            new byte[32_000],
            session.StartedAt));
        await writer.CompleteAsync();
        return session;
    }

    private static SessionSummary ToSummary(MeetingSession session) =>
        new(
            session.Id,
            session.Title,
            session.StartedAt,
            session.EndedAt,
            session.State,
            session.LocalSpeakerName);

    private sealed class PromptCapturingTransportFactory : ITranscriptionTransportFactory
    {
        public string? InitialPrompt { get; private set; }

        public Task<ITranscriptionTransport> StartAsync(
            string modelPath,
            string language,
            CancellationToken cancellationToken) =>
            StartAsync(modelPath, language, initialPrompt: null, cancellationToken);

        public Task<ITranscriptionTransport> StartAsync(
            string modelPath,
            string language,
            string? initialPrompt,
            CancellationToken cancellationToken)
        {
            InitialPrompt = initialPrompt;
            return Task.FromResult<ITranscriptionTransport>(new FakeTransport());
        }
    }

    private sealed class FakeTransport : ITranscriptionTransport
    {
        public string? VerifiedModelHash => "VERIFIED-MODEL-HASH";

        public Task<WorkerResponse> TranscribeAsync(
            string workId,
            byte[] pcm16,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerResponse(
                true,
                WorkId: workId,
                Segments: [new(0, 500, "Trazio")]));

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
