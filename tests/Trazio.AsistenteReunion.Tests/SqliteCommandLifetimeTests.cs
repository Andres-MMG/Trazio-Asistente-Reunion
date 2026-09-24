using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SqliteCommandLifetimeTests
{
    [Fact]
    public async Task ConcurrentStoreOperations_ReleaseDatabaseFilesBeforeImmediateRootDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;

        var roots = Enumerable.Range(0, 8)
            .Select(_ => Path.Combine(
                Path.GetTempPath(),
                "trazio-command-lifetime-" + Guid.NewGuid().ToString("N")))
            .ToArray();

        var operations = roots
            .Select((root, index) => Task.Run(() => ExerciseStoreAndDeleteRootAsync(root, index)))
            .ToArray();

        await Task.WhenAll(operations);

        Assert.All(roots, root => Assert.False(
            Directory.Exists(root),
            $"SQLite test root still exists after immediate deletion: {root}"));
    }

    private static async Task ExerciseStoreAndDeleteRootAsync(string root, int index)
    {
        Directory.CreateDirectory(root);
        try
        {
            using var protector = new AesContentProtector(RandomNumberGenerator.GetBytes(32));
            var store = new SqliteSessionStore(Path.Combine(root, "test.db"), protector);
            await store.InitializeAsync();

            var startedAt = DateTimeOffset.UnixEpoch.AddMinutes(index);
            var session = new MeetingSession(
                $"session-{index}",
                $"Meeting {index}",
                startedAt,
                startedAt.AddMinutes(1),
                SessionState.Completed,
                MeetingProvider: MeetingProvider.GoogleMeet);
            await store.CreateSessionAsync(session);

            var segment = new TranscriptSegment(
                $"segment-{index}",
                session.Id,
                AudioSourceKind.SystemOutput,
                index,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                $"Original {index}",
                startedAt);
            Assert.True(await store.SaveSegmentAsync(segment));
            Assert.Single(await store.GetSegmentsAsync(session.Id));

            await store.SaveCorrectionAsync(segment.Id, $"Corrected {index}", "Test editor");
            Assert.Single(await store.GetReviewedSegmentsAsync(session.Id));

            var evidence = AnonymousVisualEvidenceInterval.Activity(
                Guid.NewGuid(),
                session.Id,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                0.95,
                new(MeetingProvider.GoogleMeet, 1, 1, 1, 1));
            Assert.True(await store.SaveAnonymousVisualEvidenceAsync(evidence));
            Assert.Single((await store.GetAnonymousVisualEvidenceAsync(session.Id)).Intervals);

            var revision = await store.StartModelRevisionAsync(
                session.Id,
                AudioSourceKind.SystemOutput,
                "test-model",
                "test-hash",
                "es");
            await store.SaveModelRevisionSegmentAsync(new(
                $"revision-segment-{index}",
                revision.Id,
                index,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                $"Revision {index}"));
            await store.FinishModelRevisionAsync(
                revision.Id,
                ModelRevisionStatus.Succeeded,
                startedAt.AddSeconds(2),
                TimeSpan.FromSeconds(1),
                null);
            Assert.Single(await store.ListModelRevisionsAsync(session.Id));
            Assert.Single(await store.GetModelRevisionSegmentsAsync(revision.Id));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception exception)
            {
                throw new IOException(
                    $"Failed to delete SQLite lifetime test root immediately: {root}",
                    exception);
            }
        }
    }
}
