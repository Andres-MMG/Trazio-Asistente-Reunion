using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class TranscriptRefinementStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-refinement-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private string DatabasePath => Path.Combine(_directory, "test.db");
    private const string ConfigFingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _protector = new AesContentProtector(_key);
        _store = new SqliteSessionStore(DatabasePath, _protector);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        CryptographicOperations.ZeroMemory(_key);
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateRefinementBatch_EncryptsOriginalProposalsAndGeneratorAndKeepsSourceTiming()
    {
        var segment = await CreateSegmentAsync();
        var saved = await _store.CreateRefinementBatchAsync(segment.Id, "private-generator", ConfigFingerprint,
            [new("propuesta muy privada"), new("alternativa muy privada", true)], DateTimeOffset.UtcNow);

        var restored = Assert.Single(await _store.ListRefinementBatchesAsync(segment.SessionId));
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));
        Assert.Equal(saved.Id, restored.Id);
        Assert.Equal(segment.Text, restored.OriginalText);
        Assert.Equal(segment.Source, restored.Source);
        Assert.Equal(segment.Start, restored.Start);
        Assert.Equal(segment.End, restored.End);
        Assert.Equal("private-generator", restored.GeneratorIdentity);
        Assert.Equal(ConfigFingerprint, restored.ConfigurationFingerprint);
        Assert.Equal(2, restored.Proposals.Count);
        Assert.True(restored.Proposals[1].IsAmbiguousAlternative);
        Assert.Equal(RefinementReviewStatus.Unreviewed, restored.Proposals[0].ReviewStatus);
        Assert.DoesNotContain("propuesta muy privada", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("private-generator", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ConfigFingerprint, raw, StringComparison.Ordinal);
        Assert.Equal(segment.Text, Assert.Single(await _store.GetSegmentsAsync(segment.SessionId)).Text);
    }

    [Fact]
    public async Task CreateRefinementBatch_ZeroProposals_PersistsExplicitNoChange()
    {
        var segment = await CreateSegmentAsync();
        await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint, [], DateTimeOffset.UtcNow);
        Assert.Empty(Assert.Single(await _store.ListRefinementBatchesAsync(segment.SessionId)).Proposals);
    }

    [Fact]
    public async Task CreateRefinementBatch_OnActiveSessionOrMissingSegment_FailsWithoutRows()
    {
        var session = new MeetingSession("active", "Active", DateTimeOffset.UtcNow, null, SessionState.Recording);
        await _store.CreateSessionAsync(session);
        await _store.SaveSegmentAsync(new("active-segment", session.Id, AudioSourceKind.Microphone, 0,
            TimeSpan.Zero, TimeSpan.FromSeconds(3), "active text", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateRefinementBatchAsync(
            "active-segment", "generator", ConfigFingerprint, [new("new text")], DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateRefinementBatchAsync(
            "not-found", "generator", ConfigFingerprint, [new("new text")], DateTimeOffset.UtcNow));
        Assert.Equal(0, await CountAsync("transcript_refinement_batches"));
    }

    [Fact]
    public async Task AcceptRefinementProposal_SavesCorrectionAndReviewInOneTransaction()
    {
        var segment = await CreateSegmentAsync();
        var batch = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("texto corregido")], DateTimeOffset.UtcNow);
        var proposal = Assert.Single(batch.Proposals);
        var reviewed = await _store.AcceptRefinementProposalAsync(
            proposal.Id, expectedSegmentRevision: 0, "Andrés", DateTimeOffset.UtcNow);
        var restored = Assert.Single(Assert.Single(await _store.ListRefinementBatchesAsync(segment.SessionId)).Proposals);
        var correction = Assert.Single(await _store.GetCorrectionsAsync(segment.Id));

        Assert.Equal(RefinementReviewStatus.Accepted, restored.ReviewStatus);
        Assert.Equal(correction.Id, restored.SourceCorrectionId);
        Assert.Equal(proposal.Text, correction.CorrectedText);
        Assert.Equal("Andrés", restored.ReviewerName);
        Assert.Equal(reviewed.Id, restored.Id);
        Assert.Equal(segment.Text, Assert.Single(await _store.GetSegmentsAsync(segment.SessionId)).Text);
    }

    [Fact]
    public async Task ReviewRefinementProposal_LegacyAcceptedPath_IsRejectedEvenWithStaleCorrectionId()
    {
        var segment = await CreateSegmentAsync();
        var batch = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("texto corregido")], DateTimeOffset.UtcNow);
        var correction = await _store.SaveCorrectionAsync(segment.Id, "texto corregido", "Andrés");
        await _store.UndoCorrectionAsync(segment.Id, "Andrés");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ReviewRefinementProposalAsync(
            batch.Proposals[0].Id, RefinementReviewStatus.Accepted, "Andrés", DateTimeOffset.UtcNow, correction.Id));

        Assert.Equal(2, (await _store.GetCorrectionsAsync(segment.Id)).Count);
        Assert.Equal(RefinementReviewStatus.Unreviewed,
            Assert.Single(Assert.Single(await _store.ListRefinementBatchesAsync(segment.SessionId)).Proposals).ReviewStatus);
    }

    [Fact]
    public async Task AcceptRefinementProposal_RejectsStaleRevisionAfterUndoThenAllowsCurrentRevision()
    {
        var segment = await CreateSegmentAsync();
        var batch = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("texto corregido")], DateTimeOffset.UtcNow);
        await _store.SaveCorrectionAsync(segment.Id, "otra frase", "Andrés");
        await _store.UndoCorrectionAsync(segment.Id, "Andrés");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AcceptRefinementProposalAsync(
            batch.Proposals[0].Id, 1, "Andrés", DateTimeOffset.UtcNow));
        Assert.Equal(2, (await _store.GetCorrectionsAsync(segment.Id)).Count);
        var accepted = await _store.AcceptRefinementProposalAsync(
            batch.Proposals[0].Id, 2, "Andrés", DateTimeOffset.UtcNow);

        Assert.Equal(RefinementReviewStatus.Accepted, accepted.ReviewStatus);
        Assert.Equal(3, (await _store.GetCorrectionsAsync(segment.Id)).Count);
    }

    [Fact]
    public async Task AcceptRefinementProposal_ActiveHumanEdit_IsNeverOverwritten()
    {
        var segment = await CreateSegmentAsync();
        var batch = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("texto corregido")], DateTimeOffset.UtcNow);
        await _store.SaveCorrectionAsync(segment.Id, "otra frase", "Andrés");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AcceptRefinementProposalAsync(
            batch.Proposals[0].Id, 1, "Andrés", DateTimeOffset.UtcNow));

        Assert.Equal("otra frase", Assert.Single(await _store.GetCorrectionsAsync(segment.Id)).CorrectedText);
        Assert.Equal(RefinementReviewStatus.Unreviewed,
            Assert.Single(Assert.Single(await _store.ListRefinementBatchesAsync(segment.SessionId)).Proposals).ReviewStatus);
    }

    [Fact]
    public async Task AcceptRefinementProposal_StaleRevisionAcrossBatches_RollsBackWithoutNewCorrection()
    {
        var segment = await CreateSegmentAsync();
        var first = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("primera propuesta")], DateTimeOffset.UtcNow);
        var second = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("segunda propuesta")], DateTimeOffset.UtcNow);
        await _store.AcceptRefinementProposalAsync(first.Proposals[0].Id, 0, "Andrés", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AcceptRefinementProposalAsync(
            second.Proposals[0].Id, 0, "Andrés", DateTimeOffset.UtcNow));
        Assert.Single(await _store.GetCorrectionsAsync(segment.Id));
        Assert.Equal(RefinementReviewStatus.Unreviewed,
            (await _store.ListRefinementBatchesAsync(segment.SessionId))[1].Proposals[0].ReviewStatus);
    }

    [Fact]
    public async Task AcceptRefinementProposal_PreCancelledOperation_DoesNotWriteAnything()
    {
        var segment = await CreateSegmentAsync();
        var batch = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("texto corregido")], DateTimeOffset.UtcNow);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.AcceptRefinementProposalAsync(
            batch.Proposals[0].Id, 0, "Andrés", DateTimeOffset.UtcNow, cancel.Token));
        Assert.Empty(await _store.GetCorrectionsAsync(segment.Id));
        Assert.Equal(RefinementReviewStatus.Unreviewed,
            Assert.Single(Assert.Single(await _store.ListRefinementBatchesAsync(segment.SessionId)).Proposals).ReviewStatus);
    }
    [Fact]
    public async Task AcceptRefinementProposal_FailureAfterCorrectionInsert_RollsBackBothWrites()
    {
        var segment = await CreateSegmentAsync();
        var batch = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("texto corregido")], DateTimeOffset.UtcNow);
        var faulting = new SqliteSessionStore(DatabasePath, new FaultingProtector(_protector));

        await Assert.ThrowsAsync<InvalidOperationException>(() => faulting.AcceptRefinementProposalAsync(
            batch.Proposals[0].Id, 0, "Andrés", DateTimeOffset.UtcNow));

        Assert.Empty(await _store.GetCorrectionsAsync(segment.Id));
        Assert.Equal(RefinementReviewStatus.Unreviewed,
            Assert.Single(Assert.Single(await _store.ListRefinementBatchesAsync(segment.SessionId)).Proposals).ReviewStatus);
    }

    [Fact]
    public async Task AcceptRefinementProposal_ApprovedOriginal_IsNotOverwritten()
    {
        var segment = await CreateSegmentAsync();
        var batch = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("texto corregido")], DateTimeOffset.UtcNow);
        await _store.ApproveOriginalSegmentAsync(segment.SessionId, segment.Id, 0, "Andrés");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AcceptRefinementProposalAsync(
            batch.Proposals[0].Id, 0, "Andrés", DateTimeOffset.UtcNow));
        Assert.Empty(await _store.GetCorrectionsAsync(segment.Id));
    }

    [Fact]
    public async Task AcceptRefinementProposal_ConcurrentStaleRevision_OnlyOneCanCommit()
    {
        var segment = await CreateSegmentAsync();
        var first = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("primera propuesta")], DateTimeOffset.UtcNow);
        var second = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("segunda propuesta")], DateTimeOffset.UtcNow);
        async Task<bool> AttemptAsync(string id)
        {
            try
            {
                await _store.AcceptRefinementProposalAsync(id, 0, "Andrés", DateTimeOffset.UtcNow);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        var outcomes = await Task.WhenAll(
            AttemptAsync(first.Proposals[0].Id), AttemptAsync(second.Proposals[0].Id));

        Assert.Single(outcomes, value => value);
        Assert.Single(await _store.GetCorrectionsAsync(segment.Id));
        Assert.Single((await _store.ListRefinementBatchesAsync(segment.SessionId))
            .SelectMany(batch => batch.Proposals), proposal => proposal.ReviewStatus == RefinementReviewStatus.Accepted);
    }
    [Fact]
    public async Task ReviewRefinementProposal_RejectsWithoutCorrectionAndCascadesOnSessionDelete()
    {
        var segment = await CreateSegmentAsync();
        var batch = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("new private text")], DateTimeOffset.UtcNow);
        var proposal = Assert.Single(batch.Proposals);
        await _store.ReviewRefinementProposalAsync(proposal.Id, RefinementReviewStatus.Rejected,
            "private reviewer", DateTimeOffset.UtcNow);
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));
        Assert.DoesNotContain("private reviewer", raw, StringComparison.Ordinal);
        await _store.DeleteSessionAsync(segment.SessionId);
        Assert.Equal(0, await CountAsync("transcript_refinement_batches"));
        Assert.Equal(0, await CountAsync("transcript_refinement_proposals"));
    }

    [Fact]
    public async Task DeleteSession_WithAcceptedProposal_CascadesAllEvidence()
    {
        var segment = await CreateSegmentAsync();
        var batch = await _store.CreateRefinementBatchAsync(segment.Id, "generator", ConfigFingerprint,
            [new("texto corregido")], DateTimeOffset.UtcNow);
        await _store.AcceptRefinementProposalAsync(batch.Proposals[0].Id,
            0, "Andrés", DateTimeOffset.UtcNow);

        await _store.DeleteSessionAsync(segment.SessionId);

        Assert.Equal(0, await CountAsync("transcript_refinement_batches"));
        Assert.Equal(0, await CountAsync("transcript_refinement_proposals"));
    }
    private sealed class FaultingProtector(IContentProtector inner) : IContentProtector
    {
        public EncryptedPayload Protect(ReadOnlySpan<byte> plaintext, string associatedData)
        {
            if (associatedData.StartsWith("refinement-proposal:", StringComparison.Ordinal) &&
                associatedData.EndsWith(":reviewer", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected failure after correction insert.");
            return inner.Protect(plaintext, associatedData);
        }

        public byte[] Unprotect(EncryptedPayload payload, string associatedData) =>
            inner.Unprotect(payload, associatedData);
    }
    private async Task<TranscriptSegment> CreateSegmentAsync()
    {
        var session = new MeetingSession("session", "Meeting", DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow, SessionState.Completed);
        await _store.CreateSessionAsync(session);
        var segment = new TranscriptSegment("segment", session.Id, AudioSourceKind.SystemOutput, 1,
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), "frase original privada", DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(segment);
        return segment;
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
