using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class StorageMigrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trazio-storage-" + Guid.NewGuid().ToString("N"));

    public StorageMigrationServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void ResolveActiveRoot_WithoutConfiguration_UsesDefaultAndCreatesIt()
    {
        var locator = new FakeLocator();
        var expected = Path.Combine(_root, "default", ApplicationPaths.ProductFolderName);

        var actual = Service(locator).ResolveActiveRoot(expected, InstallDirectory());

        Assert.Equal(Path.GetFullPath(expected), actual);
        Assert.True(Directory.Exists(expected));
        Assert.Null(locator.State.ActiveRoot);
    }

    [Fact]
    public void ResolveActiveRoot_WithAvailableCustomLocation_UsesCustom()
    {
        var custom = CreateDirectory("custom");
        var locator = new FakeLocator(new(custom, null, null));

        var actual = Service(locator).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory());

        Assert.Equal(Path.GetFullPath(custom), actual);
    }

    [Fact]
    public void ResolveActiveRoot_WithUnavailableCustomLocation_FailsClosed()
    {
        var missing = Path.Combine(_root, "missing");
        var locator = new FakeLocator(new(missing, null, null));

        var error = Assert.Throws<InvalidOperationException>(() => Service(locator).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));

        Assert.Contains("no está disponible", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData(@"\\server\share\Trazio Asistente Reunion")]
    [InlineData("relative-folder")]
    public void Schedule_WithNonLocalOrRelativeTarget_IsRejected(string target)
    {
        var source = CreateDirectory("source");
        var error = Assert.Throws<InvalidOperationException>(() => Service(new FakeLocator()).Schedule(source, target, InstallDirectory()));
        Assert.Contains("unidad local fija", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schedule_WithSameOrNestedOrInstallTarget_IsRejected()
    {
        var source = CreateDirectory("source");
        var install = InstallDirectory();
        var service = Service(new FakeLocator());

        Assert.Throws<InvalidOperationException>(() => service.Schedule(source, source, install));
        Assert.Throws<InvalidOperationException>(() => service.Schedule(source, Path.Combine(source, "child"), install));
        Assert.Throws<InvalidOperationException>(() => service.Schedule(source, Path.Combine(install, "data"), install));
    }

    [Fact]
    public void Schedule_WithDriveRootOrNonEmptyDestination_IsRejected()
    {
        var source = CreateDirectory("source");
        var destination = CreateDirectory("destination");
        File.WriteAllText(Path.Combine(destination, "unrelated.txt"), "do not touch");
        var service = Service(new FakeLocator());

        Assert.Throws<InvalidOperationException>(() => service.Schedule(source, Path.GetPathRoot(source)!, InstallDirectory()));
        var error = Assert.Throws<InvalidOperationException>(() => service.Schedule(source, destination, InstallDirectory()));
        Assert.Contains("vacía", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schedule_WithUnavailableSpaceOrWriteAccess_IsRejected()
    {
        var source = CreateDirectory("source");
        File.WriteAllBytes(Path.Combine(source, "master.key"), new byte[100]);
        var target = Path.Combine(_root, "target");

        var spaceLocator = new FakeLocator(new(source, null, null));
        var spaceService = Service(spaceLocator, new FakePlatform(availableBytes: 10));
        spaceService.Schedule(source, target, InstallDirectory());
        var spaceError = Assert.Throws<InvalidOperationException>(() => spaceService.ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));
        Assert.Contains("libres", spaceError.Message, StringComparison.OrdinalIgnoreCase);
        var writeError = Assert.Throws<InvalidOperationException>(() => Service(new FakeLocator(), new FakePlatform(writeFailure: true)).Schedule(source, target, InstallDirectory()));
        Assert.Contains("No se puede escribir", writeError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePending_CopiesAllManagedFilesVerifiesAndCommitsBeforeCleanup()
    {
        var source = CreateManagedSource();
        var target = Path.Combine(_root, "target");
        var locator = new FakeLocator(new(source, null, null));
        var service = Service(locator);
        var intent = service.Schedule(source, target, InstallDirectory());

        var active = service.ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory());

        Assert.Equal(Path.GetFullPath(target), active);
        Assert.Equal(Path.GetFullPath(target), locator.State.ActiveRoot);
        Assert.Null(locator.State.Pending);
        Assert.Null(locator.State.CleanupPendingSource);
        Assert.True(Directory.Exists(source));
        Assert.False(File.Exists(Path.Combine(source, "master.key")));
        Assert.True(File.Exists(Path.Combine(source, "audio", "notes.tmp")));
        Assert.True(File.Exists(Path.Combine(source, "models", "work.partial")));
        Assert.True(File.Exists(Path.Combine(source, "other", "deep", "keep.txt")));
        Assert.Equal("db", File.ReadAllText(Path.Combine(target, "trazio-transcripts.db")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(target, "audio", "m", "0001.bin")));
        Assert.Equal(2_500_000, new FileInfo(Path.Combine(target, "models", "model.bin")).Length);
        Assert.DoesNotContain(Directory.EnumerateFiles(target, "*.partial", SearchOption.AllDirectories), _ => true);
        Assert.Equal(intent.TargetRoot, active);
    }

    [Fact]
    public void ResolvePending_WithDifferentDestinationFile_RejectsConflictAndDoesNotCommit()
    {
        var source = CreateManagedSource();
        var target = Path.Combine(_root, "target");
        var locator = new FakeLocator(new(source, null, null));
        var service = Service(locator);
        service.Schedule(source, target, InstallDirectory());
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "master.key"), "different");

        var error = Assert.Throws<IOException>(() => service.ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));

        Assert.Contains("conflict", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source, locator.State.ActiveRoot);
        Assert.NotNull(locator.State.Pending);
        Assert.True(File.Exists(Path.Combine(source, "trazio-transcripts.db")));
    }

    [Fact]
    public void ResolvePending_AfterInjectedInterruption_ResumesIdempotently()
    {
        var source = CreateManagedSource();
        var target = Path.Combine(_root, "target");
        var locator = new FakeLocator(new(source, null, null));
        var planner = Service(locator);
        planner.Schedule(source, target, InstallDirectory());
        var observer = new ThrowingObserver(2);

        Assert.Throws<InjectedStorageFailure>(() => Service(locator, observer: observer).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));
        Assert.NotNull(locator.State.Pending);
        Assert.Equal(source, locator.State.ActiveRoot);

        var active = Service(locator).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory());

        Assert.Equal(Path.GetFullPath(target), active);
        Assert.Null(locator.State.Pending);
        Assert.True(Directory.Exists(source));
        Assert.False(File.Exists(Path.Combine(source, "master.key")));
        Assert.True(File.Exists(Path.Combine(source, "audio", "notes.tmp")));
        Assert.True(File.Exists(Path.Combine(source, "models", "work.partial")));
        Assert.True(File.Exists(Path.Combine(source, "other", "deep", "keep.txt")));
    }

    [Fact]
    public void ResolveActiveRoot_CleanupResume_DeletesOnlyVerifiedManagedFilesAndPreservesUnrelated()
    {
        var source = CreateDirectory("old");
        var target = CreateDirectory("active");
        File.WriteAllText(Path.Combine(source, "master.key"), "key");
        File.WriteAllText(Path.Combine(target, "master.key"), "key");
        File.WriteAllText(Path.Combine(source, "unrelated.txt"), "keep");
        var manifest = new[] { ManifestEntry(source, "master.key") };
        var intent = new StorageMigrationIntent("cleanup-resume", source, target, manifest);
        var locator = new FakeLocator(new(target, intent, source));

        var active = Service(locator).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory());

        Assert.Equal(target, active);
        Assert.False(File.Exists(Path.Combine(source, "master.key")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, "unrelated.txt")));
        Assert.Null(locator.State.Pending);
        Assert.Null(locator.State.CleanupPendingSource);
    }

    [Fact]
    public void CancelScheduled_RemovesOnlyMatchingCopiesAndKeepsSource()
    {
        var source = CreateManagedSource();
        var target = Path.Combine(_root, "target");
        var locator = new FakeLocator(new(source, null, null));
        var service = Service(locator);
        service.Schedule(source, target, InstallDirectory());
        Assert.Throws<InjectedStorageFailure>(() =>
            Service(locator, observer: new ThrowingObserver(1)).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));
        var intent = locator.State.Pending!;
        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(source, "master.key"), Path.Combine(target, "master.key"));

        service.CancelScheduled(intent);

        Assert.Null(locator.State.Pending);
        Assert.True(File.Exists(Path.Combine(source, "master.key")));
        Assert.False(File.Exists(Path.Combine(target, "master.key")));
    }

    [Fact]
    public void ApplicationPaths_ConfigureOnce_RejectsSecondConfiguration()
    {
        var first = Path.Combine(_root, "configured-once");
        ApplicationPaths.ConfigureOnce(first);
        Assert.Equal(Path.GetFullPath(first), ApplicationPaths.DataDirectory);
        Assert.Equal(Path.Combine(first, "audio"), ApplicationPaths.AudioDirectory);
        Assert.Throws<InvalidOperationException>(() => ApplicationPaths.ConfigureOnce(Path.Combine(_root, "other")));
    }

    [Fact]
    public void ResolvePending_WithUnrelatedNestedAudioFile_RejectsDestination()
    {
        var source = CreateManagedSource();
        var target = CreateDirectory("target");
        var intent = new StorageMigrationIntent("resume-id", source, target);
        var locator = new FakeLocator(new(source, intent, null));
        Directory.CreateDirectory(Path.Combine(target, "audio"));
        File.WriteAllText(Path.Combine(target, "audio", "unrelated.txt"), "foreign");

        var error = Assert.Throws<InvalidOperationException>(() => Service(locator).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));

        Assert.Contains("no pertenecen", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source, locator.State.ActiveRoot);
        Assert.NotNull(locator.State.Pending);
    }
    [Fact]
    public void TargetUnderParent_WithDriveRoot_PreservesAbsoluteRoot()
    {
        var driveRoot = Path.GetPathRoot(_root)!;

        var target = StorageMigrationService.TargetUnderParent(driveRoot);

        Assert.Equal(Path.Combine(driveRoot, ApplicationPaths.ProductFolderName), target);
        Assert.True(Path.IsPathFullyQualified(target));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Schedule_WithNonFixedOrUnavailableDrive_IsRejected(bool localFixed, bool ready)
    {
        var source = CreateDirectory("source");
        var target = Path.Combine(_root, "target");

        var error = Assert.Throws<InvalidOperationException>(() =>
            Service(new FakeLocator(), new FakePlatform(localFixed: localFixed, ready: ready)).Schedule(source, target, InstallDirectory()));

        Assert.Contains("unidad local fija y disponible", error.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void ResolveActiveRoot_WithDestinationMismatchDuringCleanup_PreservesSourceAndIntent()
    {
        var source = CreateDirectory("cleanup-source");
        var target = CreateDirectory("cleanup-target");
        File.WriteAllText(Path.Combine(source, "master.key"), "verified");
        var manifest = new[] { ManifestEntry(source, "master.key") };
        File.WriteAllText(Path.Combine(target, "master.key"), "tampered");
        var intent = new StorageMigrationIntent("cleanup-mismatch", source, target, manifest);
        var locator = new FakeLocator(new(target, intent, source));

        Assert.Throws<IOException>(() =>
            Service(locator).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));

        Assert.Equal("verified", File.ReadAllText(Path.Combine(source, "master.key")));
        Assert.NotNull(locator.State.Pending);
        Assert.Equal(source, locator.State.CleanupPendingSource);
    }

    [Fact]
    public void ResolveActiveRoot_WhenCleanupDeleteIsInterrupted_ResumesWithoutDeletingExcludedFiles()
    {
        var source = CreateDirectory("locked-source");
        var target = CreateDirectory("locked-target");
        File.WriteAllText(Path.Combine(source, "master.key"), "key");
        File.WriteAllText(Path.Combine(source, "notes.tmp"), "keep");
        File.Copy(Path.Combine(source, "master.key"), Path.Combine(target, "master.key"));
        var manifest = new[] { ManifestEntry(source, "master.key") };
        var intent = new StorageMigrationIntent("locked-cleanup", source, target, manifest);
        var locator = new FakeLocator(new(target, intent, source));

        using (File.Open(Path.Combine(source, "master.key"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() =>
                Service(locator).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));
        }

        var active = Service(locator).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory());

        Assert.Equal(target, active);
        Assert.False(File.Exists(Path.Combine(source, "master.key")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, "notes.tmp")));
        Assert.Null(locator.State.Pending);
    }

    [Theory]
    [InlineData("Schedule", false)]
    [InlineData("Schedule", true)]
    [InlineData("PrepareMigration", false)]
    [InlineData("PrepareMigration", true)]
    [InlineData("Commit", false)]
    [InlineData("Commit", true)]
    [InlineData("CompleteCleanup", false)]
    [InlineData("CompleteCleanup", true)]
    public void StateTransitionFaults_AreRecoverableWithoutLosingTargetOrCleanupIntent(string operation, bool afterApply)
    {
        var source = CreateManagedSource();
        var target = Path.Combine(_root, "fault-target");
        var locator = new FaultingLocator(new(source, null, null), operation, afterApply);
        var service = Service(locator);

        if (operation == "Schedule")
        {
            Assert.Throws<InjectedStorageFailure>(() => service.Schedule(source, target, InstallDirectory()));
            if (!afterApply)
            {
                Assert.Null(locator.State.Pending);
                Assert.True(File.Exists(Path.Combine(source, "master.key")));
                return;
            }
        }
        else
        {
            service.Schedule(source, target, InstallDirectory());
            if (operation is "PrepareMigration" or "Commit")
                Assert.Throws<InjectedStorageFailure>(() =>
                    service.ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));
            else
                Assert.Equal(Path.GetFullPath(target),
                    service.ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory()));
        }

        var active = Service(locator).ResolveActiveRoot(Path.Combine(_root, "default"), InstallDirectory());

        Assert.Equal(Path.GetFullPath(target), active);
        Assert.True(File.Exists(Path.Combine(target, "master.key")));
        Assert.False(File.Exists(Path.Combine(source, "master.key")));
        Assert.True(File.Exists(Path.Combine(source, "audio", "notes.tmp")));
        Assert.True(File.Exists(Path.Combine(source, "models", "work.partial")));
        Assert.Null(locator.State.Pending);
        Assert.Null(locator.State.CleanupPendingSource);
    }
    private static StorageManagedFile ManifestEntry(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        using var stream = File.OpenRead(path);
        return new(relativePath, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }
    private StorageMigrationService Service(IDataRootLocator locator, FakePlatform? platform = null, IStorageMigrationObserver? observer = null) =>
        new(locator, platform ?? new FakePlatform(), observer);

    private string InstallDirectory() => CreateDirectory("install");
    private string CreateDirectory(string name) { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }

    private string CreateManagedSource()
    {
        var source = CreateDirectory("source");
        File.WriteAllText(Path.Combine(source, "trazio-transcripts.db"), "db");
        File.WriteAllText(Path.Combine(source, "trazio-transcripts.db-wal"), "wal");
        File.WriteAllText(Path.Combine(source, "master.key"), "key");
        File.WriteAllText(Path.Combine(source, "settings.dat"), "settings");
        Directory.CreateDirectory(Path.Combine(source, "audio", "m"));
        File.WriteAllBytes(Path.Combine(source, "audio", "m", "0001.bin"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(source, "models"));
        using var model = new FileStream(Path.Combine(source, "models", "model.bin"), FileMode.Create, FileAccess.Write);
        model.SetLength(2_500_000);
        File.WriteAllText(Path.Combine(source, "instance.lock"), "legacy");
        File.WriteAllText(Path.Combine(source, "audio", "stale.partial"), "stale");
        File.WriteAllText(Path.Combine(source, "audio", "notes.tmp"), "notes");
        File.WriteAllText(Path.Combine(source, "models", "work.partial"), "work");
        Directory.CreateDirectory(Path.Combine(source, "other", "deep"));
        File.WriteAllText(Path.Combine(source, "other", "deep", "keep.txt"), "keep");
        return source;
    }

    private sealed class FakeLocator : IDataRootLocator
    {
        public FakeLocator(StorageLocationState? state = null) => State = state ?? new(null, null, null);
        public StorageLocationState State { get; private set; }
        public void Schedule(StorageMigrationIntent intent) => State = State with { Pending = intent };
        public void PrepareMigration(StorageMigrationIntent intent) => State = State with { Pending = intent };
        public void CancelPending() => State = State with { Pending = null };
        public void Commit(StorageMigrationIntent intent) => State = new(intent.TargetRoot, intent, intent.SourceRoot);
        public void CompleteCleanup() => State = State with { Pending = null, CleanupPendingSource = null };
        public StorageLocationState Load() => State;
    }

    private sealed class FaultingLocator(
        StorageLocationState state,
        string faultOperation,
        bool faultAfterApply) : IDataRootLocator
    {
        private bool _faultArmed = true;
        public StorageLocationState State { get; private set; } = state;

        public StorageLocationState Load() => State;
        public void Schedule(StorageMigrationIntent intent) =>
            Transition(nameof(Schedule), () => State = State with { Pending = intent });
        public void PrepareMigration(StorageMigrationIntent intent) =>
            Transition(nameof(PrepareMigration), () => State = State with { Pending = intent });
        public void CancelPending() =>
            Transition(nameof(CancelPending), () => State = State with { Pending = null });
        public void Commit(StorageMigrationIntent intent) =>
            Transition(nameof(Commit), () => State = new(intent.TargetRoot, intent, intent.SourceRoot));
        public void CompleteCleanup() =>
            Transition(nameof(CompleteCleanup), () => State = State with { Pending = null, CleanupPendingSource = null });

        private void Transition(string operation, Action apply)
        {
            var shouldFault = _faultArmed && operation == faultOperation;
            if (shouldFault && !faultAfterApply)
            {
                _faultArmed = false;
                throw new InjectedStorageFailure();
            }

            apply();

            if (shouldFault)
            {
                _faultArmed = false;
                throw new InjectedStorageFailure();
            }
        }
    }
    private sealed class FakePlatform(long availableBytes = long.MaxValue, bool localFixed = true, bool ready = true, bool writeFailure = false) : IStoragePlatform
    {
        public StorageDriveStatus InspectDrive(string path) => new(localFixed, ready, availableBytes);
        public void VerifyWritableDirectory(string path)
        {
            if (writeFailure) throw new UnauthorizedAccessException("denied");
            Directory.CreateDirectory(path);
        }
    }

    private sealed class ThrowingObserver(int failAt) : IStorageMigrationObserver
    {
        private int _count;
        public void BeforeCopy(string relativePath)
        {
            if (Interlocked.Increment(ref _count) == failAt) throw new InjectedStorageFailure();
        }
    }

    private sealed class InjectedStorageFailure : Exception;
}









