using System.IO.Compression;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Model.Models.Storage;
using UniversalBackupTool.Service.Services.Backups;
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

            await snapshotService.PurgeUnregisteredFilesAsync(rootPath, baseline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fakeSafetyBackup.CallCount, Is.EqualTo(1));
                Assert.That(File.Exists(removeFile), Is.False);
                Assert.That(File.Exists(keepFile), Is.True);
            }
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
                    Assert.That(actual.SafetyBackupRetentionDays, Is.EqualTo(expected.SafetyBackupRetentionDays));
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
                }
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
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
            var service = new SafetyBackupService(storageProvider);

            var archivePath = await service.CreateSafetyBackupAsync(musicRoot, "pre-purge");

            Assert.That(File.Exists(archivePath), Is.True);

            using var zip = ZipFile.OpenRead(archivePath);

            Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("track1.txt"));
            Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("nested/track2.txt"));
        }
    }

    [TestFixture]
    public class BackupEngineTests
    {
        [Test]
        public async Task CreateBackupArchiveAsync_CreatesArchiveWithExpectedEntries()
        {
            var rootPath = TestDirectoryFactory.CreateTemporaryDirectory();
            var fileA = Path.Combine(rootPath, "mod.txt");
            var fileB = Path.Combine(rootPath, "nested", "data.txt");

            Directory.CreateDirectory(Path.GetDirectoryName(fileB)!);

            await File.WriteAllTextAsync(fileA, "keep");
            await File.WriteAllTextAsync(fileB, "payload");

            var archivePath = Path.Combine(rootPath, "archive.zip");
            var settings = AppSettings.Default(Path.Combine(rootPath, "backups"));
            var engine = new BackupEngine(new FakeStoragePathsProvider(rootPath), new FakeFileSystemProvider());

            var result = await engine.CreateBackupArchiveAsync(rootPath, archivePath, settings);

            Assert.That(result, Is.EqualTo(archivePath));
            Assert.That(File.Exists(archivePath), Is.True);

            using var zip = ZipFile.OpenRead(archivePath);

            Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("mod.txt"));
            Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("nested/data.txt"));
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
            }
        }
    }

    internal sealed class FakeFileSystemProvider : IFileSystemProvider
    {
        public Task EnsureDirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(path);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<string>> EnumerateFilesAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(rootPath))
                return Task.FromResult<IEnumerable<string>>([]);

            var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                .Where(path => !IsSymlink(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.FromResult<IEnumerable<string>>(files);
        }

        public string GetRelativePath(string rootPath, string filePath)
        {
            return Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
        }

        public bool IsSymlink(string path)
        {
            if (!File.Exists(path))
                return false;

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }

        public string ResolveActualPath(string path)
        {
            return Path.GetFullPath(path);
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
        {
            if (File.Exists(path))
                File.Delete(path);

            return Task.CompletedTask;
        }

        public Task DeleteDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive);

            return Task.CompletedTask;
        }
    }

    internal sealed class FakeSafetyBackupService : ISafetyBackupService
    {
        public int CallCount { get; private set; }

        public Task<string> CreateSafetyBackupAsync(string sourceRootPath, string? archiveNamePrefix = null, CancellationToken cancellationToken = default)
        {
            CallCount++;

            var archivePath = Path.Combine(sourceRootPath, "safety_backup.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

            return Task.FromResult(archivePath);
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
}