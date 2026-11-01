using System.IO.Compression;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Model.Models.Storage;
using UniversalBackupTool.Service.Services.Jobs;
using UniversalBackupTool.Service.Services.Settings;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Service.Services.Storage;
using UniversalBackupTool.Services.Backups;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class SnapshotServiceTests
    {
        [Test]
        public async Task CreateSnapshotAsync_CollectsFilesFromRootDirectory()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var fileA = Path.Combine(rootPath, "file-a.txt");
            var subDirectory = Path.Combine(rootPath, "nested");

            Directory.CreateDirectory(subDirectory);

            var fileB = Path.Combine(subDirectory, "file-b.txt");

            await File.WriteAllTextAsync(fileA, "alpha");
            await File.WriteAllTextAsync(fileB, "beta");

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());
            var snapshot = await snapshotService.CreateSnapshotAsync(Guid.NewGuid(), rootPath);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot.Files.Count, Is.EqualTo(2));
                Assert.That(snapshot.Files.ContainsKey("file-a.txt"), Is.True);
                Assert.That(snapshot.Files.ContainsKey("nested/file-b.txt"), Is.True);
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_WithEmptyDirectory_ReturnsEmptySnapshot()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var profileId = Guid.NewGuid();

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());
            var snapshot = await snapshotService.CreateSnapshotAsync(profileId, rootPath);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot.ProfileId, Is.EqualTo(profileId));
                Assert.That(snapshot.Files.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_WithNonexistentPath_ReturnsEmptySnapshot()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N"));
            var profileId = Guid.NewGuid();

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());
            var snapshot = await snapshotService.CreateSnapshotAsync(profileId, rootPath);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot.ProfileId, Is.EqualTo(profileId));
                Assert.That(snapshot.Files.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_CapturesFileMetadata()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var filePath = Path.Combine(rootPath, "test.txt");

            await File.WriteAllTextAsync(filePath, "content");

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());
            var snapshot = await snapshotService.CreateSnapshotAsync(Guid.NewGuid(), rootPath);

            Assert.That(snapshot.Files.ContainsKey("test.txt"), Is.True);

            var metadata = snapshot.Files["test.txt"];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(metadata.RelativePath, Is.EqualTo("test.txt"));
                Assert.That(metadata.SizeBytes, Is.GreaterThan(0));
                Assert.That(metadata.LastWriteTimeUtc, Is.GreaterThan(DateTime.MinValue));
                Assert.That(metadata.IsSymlink, Is.False);
            }
        }

        [Test]
        public async Task GetChangesAsync_DetectsAddedModifiedAndRemovedFiles()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var original = Path.Combine(rootPath, "keep.txt");
            var modified = Path.Combine(rootPath, "modified.txt");
            var deletedPath = Path.Combine(rootPath, "deleted.txt");

            await File.WriteAllTextAsync(original, "keep");

            var keepTimestamp = File.GetLastWriteTimeUtc(original);

            await File.WriteAllTextAsync(modified, "before");
            await File.WriteAllTextAsync(deletedPath, "remove");

            var baseline = new SnapshotData(
                Guid.NewGuid(),
                new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase)
                {
                    ["keep.txt"] = new("keep.txt", 4, keepTimestamp, false),
                    ["modified.txt"] = new("modified.txt", 6, DateTime.Now.AddMinutes(-10), false),
                    ["deleted.txt"] = new("deleted.txt", 6, DateTime.Now.AddMinutes(-20), false)
                });

            await File.WriteAllTextAsync(modified, "after-changed");
            File.Delete(deletedPath);

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());
            var changes = await snapshotService.GetChangesAsync(rootPath, baseline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(changes.ContainsKey("modified.txt"), Is.True);
                Assert.That(changes.ContainsKey("deleted.txt"), Is.True);
                Assert.That(changes.ContainsKey("keep.txt"), Is.False);
            }
        }

        [Test]
        public async Task GetChangesAsync_WithNoChanges_ReturnsEmpty()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var filePath = Path.Combine(rootPath, "file.txt");

            await File.WriteAllTextAsync(filePath, "content");

            var fileInfo = new FileInfo(filePath);
            var baseline = new SnapshotData(
                Guid.NewGuid(),
                new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase)
                {
                    ["file.txt"] = new("file.txt", fileInfo.Length, fileInfo.LastWriteTimeUtc, false)
                });

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());
            var changes = await snapshotService.GetChangesAsync(rootPath, baseline);

            Assert.That(changes.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task GetChangesAsync_DetectsNewlyAddedFiles()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var newFile = Path.Combine(rootPath, "new.txt");

            await File.WriteAllTextAsync(newFile, "new content");

            var baseline = new SnapshotData(Guid.NewGuid(), new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase));

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());
            var changes = await snapshotService.GetChangesAsync(rootPath, baseline);

            Assert.That(changes.ContainsKey("new.txt"), Is.True);
        }

        [Test]
        public async Task PurgeUnregisteredFilesAsync_DeletesUnexpectedFilesAndCreatesSafetyBackup()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var keepFile = Path.Combine(rootPath, "keep.txt");
            var removeFile = Path.Combine(rootPath, "remove.txt");

            await File.WriteAllTextAsync(keepFile, "keep");
            await File.WriteAllTextAsync(removeFile, "remove");

            var baseline = new SnapshotData(
                Guid.NewGuid(),
                new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase)
                {
                    ["keep.txt"] = new("keep.txt", 4, DateTime.UtcNow, false)
                });

            var fakeSafetyBackup = new FakeSafetyBackupService();
            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), fakeSafetyBackup);

            var result = await snapshotService.PurgeUnregisteredFilesAsync(rootPath, baseline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fakeSafetyBackup.CallCount, Is.EqualTo(1));
                Assert.That(fakeSafetyBackup.LastCategory, Is.EqualTo("Purge"));
                Assert.That(fakeSafetyBackup.LastFiles.Select(file => file.EntryName), Is.EqualTo(new[] { "remove.txt" }));
                Assert.That(result.FilesDeleted, Is.EqualTo(1));
                Assert.That(File.Exists(removeFile), Is.False);
                Assert.That(File.Exists(keepFile), Is.True);
            }
        }

        [Test]
        public async Task PurgeUnregisteredFilesAsync_WithEmptyBaseline_DeletesAllFiles()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var file1 = Path.Combine(rootPath, "file1.txt");
            var file2 = Path.Combine(rootPath, "file2.txt");

            await File.WriteAllTextAsync(file1, "content1");
            await File.WriteAllTextAsync(file2, "content2");

            var baseline = new SnapshotData(Guid.NewGuid(), new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase));

            var fakeSafetyBackup = new FakeSafetyBackupService();
            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), fakeSafetyBackup);

            await snapshotService.PurgeUnregisteredFilesAsync(rootPath, baseline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fakeSafetyBackup.CallCount, Is.EqualTo(1));
                Assert.That(File.Exists(file1), Is.False);
                Assert.That(File.Exists(file2), Is.False);
            }
        }

        [Test]
        public async Task PurgeUnregisteredFilesAsync_WithNonexistentPath_DoesNotThrow()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N"));
            var baseline = new SnapshotData(Guid.NewGuid(), new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase));

            var fakeSafetyBackup = new FakeSafetyBackupService();
            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), fakeSafetyBackup);

            await Assert.DoesNotThrowAsync(async () => await snapshotService.PurgeUnregisteredFilesAsync(rootPath, baseline));
            Assert.That(fakeSafetyBackup.CallCount, Is.EqualTo(0));
        }
    }

    [TestFixture]
    public class JsonSettingsStoreTests
    {
        [Test]
        public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
            var expected = AppSettings.Default(Path.Combine(Path.GetTempPath(), "backup-root"));
            var service = new JsonSettingsStore();

            try
            {
                await service.SaveAsync(filePath, expected);
                var actual = await service.LoadAsync(filePath);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(actual, Is.Not.Null);
                    Assert.That(actual.GoogleDriveBackupPath, Is.EqualTo(expected.GoogleDriveBackupPath));
                    Assert.That(actual.SafetyBackupPath, Is.EqualTo(expected.SafetyBackupPath));
                    Assert.That(actual.SafetyBackupRetentionDays, Is.EqualTo(expected.SafetyBackupRetentionDays));
                    Assert.That(actual.MaxPreservedBackupsPerGame, Is.EqualTo(expected.MaxPreservedBackupsPerGame));
                    Assert.That(actual.CompressionRules.Count, Is.EqualTo(expected.CompressionRules.Count));
                }
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
        }

        [Test]
        public async Task LoadAsync_WhenFileMissing_CreatesNewDefaultSettingsFile()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}");
            var filePath = Path.Combine(directory, "settings.json");
            var service = new JsonSettingsStore();

            try
            {
                var settings = await service.LoadAsync(filePath);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(File.Exists(filePath), Is.True);
                    Assert.That(settings, Is.Not.Null);
                    Assert.That(settings.GoogleDriveBackupPath, Is.EqualTo(Path.Combine(AppContext.BaseDirectory, "Backups")));
                    Assert.That(settings.MaxPreservedBackupsPerGame, Is.EqualTo(3));
                    Assert.That(string.IsNullOrWhiteSpace(settings.SafetyBackupPath), Is.False);
                }
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        [Test]
        public async Task SaveAsync_CreatesDirectoryIfMissing()
        {
            var baseDir = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}");
            var filePath = Path.Combine(baseDir, "subdir", "settings.json");
            var settings = AppSettings.Default(Path.Combine(baseDir, "backups"));
            var service = new JsonSettingsStore();

            try
            {
                await service.SaveAsync(filePath, settings);

                Assert.That(File.Exists(filePath), Is.True);
            }
            finally
            {
                if (Directory.Exists(baseDir))
                    Directory.Delete(baseDir, true);
            }
        }

        [Test]
        public async Task SaveAsync_OverwritesExistingFile()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
            var service = new JsonSettingsStore();

            try
            {
                var settings1 = AppSettings.Default(Path.Combine(Path.GetTempPath(), "backup1"));
                var settings2 = AppSettings.Default(Path.Combine(Path.GetTempPath(), "backup2"));

                await service.SaveAsync(filePath, settings1);
                await service.SaveAsync(filePath, settings2);

                var loaded = await service.LoadAsync(filePath);

                Assert.That(loaded.GoogleDriveBackupPath, Is.EqualTo(settings2.GoogleDriveBackupPath));
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
        }

        [Test]
        public async Task LoadAsync_WithThrowOnEmptyPath_Throws()
        {
            var service = new JsonSettingsStore();

            await Assert.ThrowsAsync<ArgumentException>(async () => await service.LoadAsync(""));
        }
    }

    [TestFixture]
    public class BackupJobStateServiceTests
    {
        [Test]
        public async Task SaveAsync_AndLoadAsync_RoundTripsJobState()
        {
            var job = ActiveBackupJob.Create(Guid.NewGuid(), "C:/backup/archive.zip", "C:/mods/game");
            var filePath = Path.Combine(AppContext.BaseDirectory, "Temp", "active_backup_job.json");
            var service = new BackupJobStateService();

            try
            {
                await service.SaveAsync(job);
                var loaded = await service.LoadAsync(filePath);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(loaded, Is.Not.Null);
                    Assert.That(loaded!.JobId, Is.EqualTo(job.JobId));
                    Assert.That(loaded.ProfileId, Is.EqualTo(job.ProfileId));
                    Assert.That(loaded.ArchivePath, Is.EqualTo(job.ArchivePath));
                    Assert.That(loaded.IsCompleted, Is.EqualTo(job.IsCompleted));
                }
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
        }

        [Test]
        public async Task DeleteAsync_RemovesExistingStateFile()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"job-{Guid.NewGuid():N}.json");

            await File.WriteAllTextAsync(filePath, "{}");

            var service = new BackupJobStateService();

            await service.DeleteAsync(filePath);

            Assert.That(File.Exists(filePath), Is.False);
        }

        [Test]
        public async Task LoadAsync_WithNonexistentFile_ReturnsNull()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"job-{Guid.NewGuid():N}.json");
            var service = new BackupJobStateService();

            var loaded = await service.LoadAsync(filePath);

            Assert.That(loaded, Is.Null);
        }

        [Test]
        public async Task SaveAsync_CreatesDirectoryIfMissing()
        {
            var job = ActiveBackupJob.Create(Guid.NewGuid(), "C:/archive.zip", "C:/root");
            var baseDir = Path.Combine(AppContext.BaseDirectory, "Temp");
            var service = new BackupJobStateService();

            try
            {
                await service.SaveAsync(job);

                var filePath = Path.Combine(baseDir, "active_backup_job.json");

                Assert.That(File.Exists(filePath), Is.True);
            }
            finally
            {
                var filePath = Path.Combine(baseDir, "active_backup_job.json");
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
        }
    }

    [TestFixture]
    public class SafetyBackupServiceTests
    {
        [Test]
        public async Task CreateSafetyBackupAsync_CreatesZipArchiveForDirectory()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var musicRoot = Path.Combine(baseDirectory, "music");

            Directory.CreateDirectory(musicRoot);

            var fileA = Path.Combine(musicRoot, "track1.txt");
            var fileB = Path.Combine(musicRoot, "nested", "track2.txt");

            Directory.CreateDirectory(Path.GetDirectoryName(fileB)!);

            await File.WriteAllTextAsync(fileA, "first");
            await File.WriteAllTextAsync(fileB, "second");

            var storageProvider = new FakeStoragePathsProvider(Path.Combine(baseDirectory, "safety-root"));
            var service = new SafetyBackupService(storageProvider, new JsonSettingsStore());

            var archivePath = await service.CreateSafetyBackupAsync(musicRoot, "pre-purge");

            Assert.That(File.Exists(archivePath), Is.True);

            using var zip = ZipFile.OpenRead(archivePath);

            Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("track1.txt"));
            Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("nested/track2.txt"));
        }

        [Test]
        public async Task CreateSafetyBackupAsync_WithEmptyDirectory_CreatesEmptyArchive()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var emptyRoot = Path.Combine(baseDirectory, "empty");
            Directory.CreateDirectory(emptyRoot);

            var storageProvider = new FakeStoragePathsProvider(Path.Combine(baseDirectory, "safety-root"));
            var service = new SafetyBackupService(storageProvider, new JsonSettingsStore());

            var archivePath = await service.CreateSafetyBackupAsync(emptyRoot, "empty-backup");

            Assert.That(File.Exists(archivePath), Is.True);

            using var zip = ZipFile.OpenRead(archivePath);

            Assert.That(zip.Entries.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task CreateSafetyBackupAsync_UsesConfiguredSafetyBackupPath()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var source = Path.Combine(baseDirectory, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "x");

            var storageProvider = new FakeStoragePathsProvider(Path.Combine(baseDirectory, "app"));
            var settingsStore = new JsonSettingsStore();
            var customSafetyPath = Path.Combine(baseDirectory, "custom-safety");
            var settingsPath = storageProvider.GetPaths().SettingsFilePath;
            var settings = AppSettings.Default(Path.Combine(baseDirectory, "backups")) with { SafetyBackupPath = customSafetyPath };
            await settingsStore.SaveAsync(settingsPath, settings);

            var service = new SafetyBackupService(storageProvider, settingsStore);
            var archivePath = await service.CreateSafetyBackupAsync(source, "configured");

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(archivePath), Is.True);
                Assert.That(archivePath.StartsWith(customSafetyPath, StringComparison.OrdinalIgnoreCase), Is.True);
                Assert.That(archivePath.Contains($"{Path.DirectorySeparatorChar}General{Path.DirectorySeparatorChar}"), Is.True);
            });
        }

        [Test]
        public async Task CreateSafetyBackupAsync_WhenSafetyPathEqualsBackupRoot_UsesSafetyBackupsSubfolder()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var source = Path.Combine(baseDirectory, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "x");

            var backupRoot = Path.Combine(baseDirectory, "Google Drive");
            var storageProvider = new FakeStoragePathsProvider(Path.Combine(baseDirectory, "app"));
            var settingsStore = new JsonSettingsStore();
            var settingsPath = storageProvider.GetPaths().SettingsFilePath;
            var settings = AppSettings.Default(backupRoot) with { SafetyBackupPath = backupRoot };
            await settingsStore.SaveAsync(settingsPath, settings);

            var service = new SafetyBackupService(storageProvider, settingsStore);
            var archivePath = await service.CreateSafetyBackupAsync(source, "configured");

            var expectedRoot = Path.Combine(backupRoot, "SafetyBackups");

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(archivePath), Is.True);
                Assert.That(archivePath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase), Is.True);
                Assert.That(archivePath.Contains($"{Path.DirectorySeparatorChar}General{Path.DirectorySeparatorChar}"), Is.True);
            });
        }

        [Test]
        public async Task CreateSafetyBackupAsync_WithNonexistentPath_ReturnsPathWithoutError()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var nonexistent = Path.Combine(baseDirectory, "nonexistent");

            var storageProvider = new FakeStoragePathsProvider(Path.Combine(baseDirectory, "safety-root"));
            var service = new SafetyBackupService(storageProvider, new JsonSettingsStore());

            var archivePath = await service.CreateSafetyBackupAsync(nonexistent, "no-source");

            Assert.That(archivePath, Is.Not.Null);
        }

        [Test]
        public async Task CleanupExpiredAsync_RemovesOldBackups()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var storageProvider = new FakeStoragePathsProvider(baseDirectory);
            var safetyDir = storageProvider.GetPaths().SafetyBackupsDirectory;

            var oldFile = Path.Combine(safetyDir, "old_backup.zip");
            var newFile = Path.Combine(safetyDir, "new_backup.zip");

            await File.WriteAllTextAsync(oldFile, "old");
            await File.WriteAllTextAsync(newFile, "new");

            File.SetLastWriteTimeUtc(oldFile, DateTime.Now.AddDays(-20));
            File.SetLastWriteTimeUtc(newFile, DateTime.Now.AddDays(-5));

            var settings = AppSettings.Default(baseDirectory) with { SafetyBackupPath = safetyDir };
            var service = new SafetyBackupService(storageProvider, new JsonSettingsStore());

            await service.CleanupExpiredAsync(settings);

            Assert.That(File.Exists(oldFile), Is.False);
            Assert.That(File.Exists(newFile), Is.True);
        }
    }

    [TestFixture]
    public class AppStoragePathsProviderTests
    {
        [Test]
        public void Constructor_StoresBaseDirectoryAndDerivedPaths()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var provider = new AppStoragePathsProvider(baseDirectory);

            var paths = provider.GetPaths();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(paths.BaseDirectory, Is.EqualTo(baseDirectory));
                Assert.That(paths.SettingsFilePath, Is.EqualTo(Path.Combine(baseDirectory, "settings.json")));
                Assert.That(paths.SafetyBackupsDirectory, Is.EqualTo(Path.Combine(baseDirectory, "SafetyBackups")));
                Assert.That(paths.TempDirectory, Is.EqualTo(Path.Combine(baseDirectory, "Temp")));
                Assert.That(paths.SnapshotsDirectory, Is.EqualTo(Path.Combine(baseDirectory, "Snapshots")));
                Assert.That(paths.LogsDirectory, Is.EqualTo(Path.Combine(baseDirectory, "Logs")));
            }
        }

        [Test]
        public void GetPaths_ReturnsConsistentPaths()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var provider = new AppStoragePathsProvider(baseDirectory);

            var paths1 = provider.GetPaths();
            var paths2 = provider.GetPaths();

            Assert.That(paths1.BaseDirectory, Is.EqualTo(paths2.BaseDirectory));
            Assert.That(paths1.SettingsFilePath, Is.EqualTo(paths2.SettingsFilePath));
        }

        [Test]
        public void Constructor_WithDefaultDirectory_UsesAppContextBaseDirectory()
        {
            var provider = new AppStoragePathsProvider();

            var paths = provider.GetPaths();

            Assert.That(paths.BaseDirectory, Is.EqualTo(AppContext.BaseDirectory));
        }
    }

    internal sealed class FakeFileSystemProvider : IFileSystemProvider
    {
        // Link handling is exercised against the real implementation.
        private static readonly UniversalBackupTool.Services.FileSystemProvider Real = new();

        public IReadOnlyList<UniversalBackupTool.Model.Models.FileSystem.ScannedEntry> ScanWithoutFollowingLinks(string rootPath, CancellationToken cancellationToken = default)
            => Real.ScanWithoutFollowingLinks(rootPath, cancellationToken);

        public UniversalBackupTool.Model.Models.FileSystem.FollowedScan ScanFollowingLinks(string rootPath, CancellationToken cancellationToken = default)
            => Real.ScanFollowingLinks(rootPath, cancellationToken);

        public void DeleteEntry(UniversalBackupTool.Model.Models.FileSystem.ScannedEntry entry) => Real.DeleteEntry(entry);
    }

    internal sealed class FakeSafetyBackupService : ISafetyBackupService
    {
        public int CallCount { get; private set; }

        public List<ArchiveFileItem> LastFiles { get; } = [];

        public string? LastCategory { get; private set; }

        public Task<string> CreateSafetyBackupAsync(string sourceRootPath, string? archiveNamePrefix = null, CancellationToken cancellationToken = default)
        {
            CallCount++;

            var archivePath = Path.Combine(sourceRootPath, "safety_backup.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

            return Task.FromResult(archivePath);
        }

        public Task<string?> CreateSafetyBackupAsync(IReadOnlyList<ArchiveFileItem> files, string archiveNamePrefix, string category, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastFiles.Clear();
            LastFiles.AddRange(files);
            LastCategory = category;

            var first = files.FirstOrDefault()?.SourcePath ?? Path.GetTempPath();
            var archivePath = Path.Combine(Path.GetDirectoryName(first) ?? Path.GetTempPath(), "safety_backup.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

            return Task.FromResult<string?>(archivePath);
        }

        public Task CleanupExpiredAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeStoragePathsProvider : IAppStoragePathsProvider
    {
        private readonly AppStoragePaths _paths;

        public FakeStoragePathsProvider(string baseDirectory)
        {
            _paths = new AppStoragePaths(baseDirectory);

            Directory.CreateDirectory(_paths.BaseDirectory);
            Directory.CreateDirectory(_paths.SafetyBackupsDirectory);
        }

        public AppStoragePaths GetPaths() => _paths;
    }

    internal static class TestDirectoryFactory
    {
        public static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), $"ubt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);

            return path;
        }
    }

    [TestFixture]
    public class SnapshotServiceEdgeCasesTests
    {
        [Test]
        public async Task CreateSnapshotAsync_WithDeeplyNestedDirectories_CapturesAllFiles()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var deepPath = Path.Combine(rootPath, "a", "b", "c", "d", "e");
            Directory.CreateDirectory(deepPath);
            var filePath = Path.Combine(deepPath, "deep.txt");
            await File.WriteAllTextAsync(filePath, "deeply nested");

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());

            var snapshot = await snapshotService.CreateSnapshotAsync(Guid.NewGuid(), rootPath);

            Assert.That(snapshot.Files.ContainsKey("a/b/c/d/e/deep.txt"), Is.True);
        }

        [Test]
        public async Task CreateSnapshotAsync_WithLargeFileCount_CapturesAll()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var fileCount = 50;

            for (int i = 0; i < fileCount; i++)
            {
                var filePath = Path.Combine(rootPath, $"file-{i:D3}.txt");
                await File.WriteAllTextAsync(filePath, $"content {i}");
            }

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());

            var snapshot = await snapshotService.CreateSnapshotAsync(Guid.NewGuid(), rootPath);

            Assert.That(snapshot.Files.Count, Is.EqualTo(fileCount));
        }

        [Test]
        public async Task GetChangesAsync_WithFileSizeChange_DetectsModification()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var filePath = Path.Combine(rootPath, "file.txt");
            await File.WriteAllTextAsync(filePath, "original");

            var fileInfo = new FileInfo(filePath);
            var baseline = new SnapshotData(
                Guid.NewGuid(),
                new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase)
                {
                    ["file.txt"] = new("file.txt", 100, fileInfo.LastWriteTimeUtc, false)
                });

            await File.WriteAllTextAsync(filePath, "x");

            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), new FakeSafetyBackupService());
            var changes = await snapshotService.GetChangesAsync(rootPath, baseline);

            Assert.That(changes.ContainsKey("file.txt"), Is.True);
        }

        [Test]
        public async Task PurgeUnregisteredFilesAsync_SkipsRegisteredFilesInNestedDirs()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var nestedDir = Path.Combine(rootPath, "nested");
            Directory.CreateDirectory(nestedDir);
            var keepFile = Path.Combine(nestedDir, "keep.txt");
            var removeFile = Path.Combine(rootPath, "remove.txt");

            await File.WriteAllTextAsync(keepFile, "keep");
            await File.WriteAllTextAsync(removeFile, "remove");

            var baseline = new SnapshotData(
                Guid.NewGuid(),
                new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nested/keep.txt"] = new("nested/keep.txt", 4, File.GetLastWriteTimeUtc(keepFile), false)
                });

            var fakeSafetyBackup = new FakeSafetyBackupService();
            var snapshotService = new SnapshotService(new FakeFileSystemProvider(), fakeSafetyBackup);

            await snapshotService.PurgeUnregisteredFilesAsync(rootPath, baseline);

            Assert.That(File.Exists(keepFile), Is.True);
            Assert.That(File.Exists(removeFile), Is.False);
        }
    }

    [TestFixture]
    public class JsonSettingsStoreEdgeCasesTests
    {
        [Test]
        public async Task LoadAsync_WithNullFilePath_Throws()
        {
            var service = new JsonSettingsStore();

            await Assert.ThrowsAsync<ArgumentException>(async () => await service.LoadAsync(null!));
        }

        [Test]
        public async Task SaveAsync_WithNullSettings_Completes()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
            var settings = AppSettings.Default(Path.Combine(Path.GetTempPath(), "backup"));
            var service = new JsonSettingsStore();

            try
            {
                await Assert.DoesNotThrowAsync(async () => await service.SaveAsync(filePath, settings));
                Assert.That(File.Exists(filePath), Is.True);
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
        }

        [Test]
        public async Task LoadAsync_TwiceSameFile_ReturnsSameValues()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
            var service = new JsonSettingsStore();

            try
            {
                var first = await service.LoadAsync(filePath);
                var second = await service.LoadAsync(filePath);

                Assert.That(first.GoogleDriveBackupPath, Is.EqualTo(second.GoogleDriveBackupPath));
                Assert.That(first.SafetyBackupRetentionDays, Is.EqualTo(second.SafetyBackupRetentionDays));
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
        }
    }

    [TestFixture]
    public class BackupJobStateServiceEdgeCasesTests
    {
        [Test]
        public async Task SaveAsync_WithMultipleJobs_OverwritesPrevious()
        {
            var job1 = ActiveBackupJob.Create(Guid.NewGuid(), "C:/archive1.zip", "C:/source");
            var job2 = ActiveBackupJob.Create(Guid.NewGuid(), "C:/archive2.zip", "C:/source");
            var filePath = Path.Combine(AppContext.BaseDirectory, "Temp", "active_backup_job.json");
            var service = new BackupJobStateService();

            try
            {
                await service.SaveAsync(job1);
                await service.SaveAsync(job2);
                var loaded = await service.LoadAsync(filePath);

                Assert.That(loaded!.JobId, Is.EqualTo(job2.JobId));
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
        }

        [Test]
        public async Task DeleteAsync_WithNonexistentFile_DoesNotThrow()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"job-{Guid.NewGuid():N}.json");
            var service = new BackupJobStateService();

            await Assert.DoesNotThrowAsync(async () => await service.DeleteAsync(filePath));
        }

        [Test]
        public async Task LoadAsync_ReturnsJobWithCorrectProperties()
        {
            var profileId = Guid.NewGuid();
            var archivePath = "C:/test/archive.zip";
            var sourceRoot = "C:/test/source";
            var job = ActiveBackupJob.Create(profileId, archivePath, sourceRoot);
            var filePath = Path.Combine(AppContext.BaseDirectory, "Temp", "active_backup_job.json");
            var service = new BackupJobStateService();

            try
            {
                await service.SaveAsync(job);
                var loaded = await service.LoadAsync(filePath);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.ProfileId, Is.EqualTo(profileId));
                Assert.That(loaded.ArchivePath, Is.EqualTo(archivePath));
                Assert.That(loaded.RootPath, Is.EqualTo(sourceRoot));
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
        }
    }

    [TestFixture]
    public class SafetyBackupServiceEdgeCasesTests
    {
        [Test]
        public async Task CreateSafetyBackupAsync_WithSpecialCharacterFilenames_CreatesArchive()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var sourceDir = Path.Combine(baseDirectory, "source");
            Directory.CreateDirectory(sourceDir);
            var specialFile = Path.Combine(sourceDir, "file-with-special_chars.txt");
            await File.WriteAllTextAsync(specialFile, "content");

            var storageProvider = new FakeStoragePathsProvider(Path.Combine(baseDirectory, "safety"));
            var service = new SafetyBackupService(storageProvider, new JsonSettingsStore());

            var archivePath = await service.CreateSafetyBackupAsync(sourceDir, "special-test");

            Assert.That(File.Exists(archivePath), Is.True);
        }

        [Test]
        public async Task CreateSafetyBackupAsync_GeneratesUniquePaths()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var sourceDir = Path.Combine(baseDirectory, "source");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "file.txt"), "content");

            var storageProvider = new FakeStoragePathsProvider(baseDirectory);
            var service = new SafetyBackupService(storageProvider, new JsonSettingsStore());

            var path1 = await service.CreateSafetyBackupAsync(sourceDir, "backup");
            await Task.Delay(1100);
            var path2 = await service.CreateSafetyBackupAsync(sourceDir, "backup");

            Assert.That(path1, Is.Not.EqualTo(path2));
        }

        [Test]
        public async Task CleanupExpiredAsync_PreservesRecentBackups()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var storageProvider = new FakeStoragePathsProvider(baseDirectory);
            var safetyDir = storageProvider.GetPaths().SafetyBackupsDirectory;

            var recentFile = Path.Combine(safetyDir, "recent_backup.zip");
            await File.WriteAllTextAsync(recentFile, "recent");
            File.SetLastWriteTimeUtc(recentFile, DateTime.Now.AddDays(-1));

            var settings = AppSettings.Default(baseDirectory) with { SafetyBackupPath = safetyDir };
            var service = new SafetyBackupService(storageProvider, new JsonSettingsStore());

            await service.CleanupExpiredAsync(settings);

            Assert.That(File.Exists(recentFile), Is.True);
        }
    }

    [TestFixture]
    public class AppStoragePathsProviderEdgeCasesTests
    {
        [Test]
        public void GetPaths_PropertiesNotNull()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var provider = new AppStoragePathsProvider(baseDirectory);

            var paths = provider.GetPaths();

            Assert.That(paths.BaseDirectory, Is.Not.Null);
            Assert.That(paths.SettingsFilePath, Is.Not.Null);
            Assert.That(paths.SafetyBackupsDirectory, Is.Not.Null);
            Assert.That(paths.TempDirectory, Is.Not.Null);
            Assert.That(paths.SnapshotsDirectory, Is.Not.Null);
            Assert.That(paths.LogsDirectory, Is.Not.Null);
        }

        [Test]
        public void GetPaths_AllPathsUnderBaseDirectory()
        {
            var baseDirectory = TestDirectoryFactory.CreateTemporaryDirectory();
            var provider = new AppStoragePathsProvider(baseDirectory);

            var paths = provider.GetPaths();

            Assert.That(paths.SettingsFilePath.StartsWith(baseDirectory), Is.True);
            Assert.That(paths.SafetyBackupsDirectory.StartsWith(baseDirectory), Is.True);
            Assert.That(paths.TempDirectory.StartsWith(baseDirectory), Is.True);
            Assert.That(paths.SnapshotsDirectory.StartsWith(baseDirectory), Is.True);
            Assert.That(paths.LogsDirectory.StartsWith(baseDirectory), Is.True);
        }
    }
}