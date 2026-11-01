using System.IO.Compression;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Settings;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Services;
using UniversalBackupTool.Services.Backups;

namespace UniversalBackupTool.Tests
{
    /// <summary>
    /// Faster backups: file types stored without compression, a size limit above which a whole component is stored,
    /// and archives written in parallel.
    /// </summary>
    [TestFixture]
    public class CompressionSpeedTests
    {
        private string _root = string.Empty;
        private string _staging = string.Empty;
        private string _drive = string.Empty;
        private FakeStoragePathsProvider _paths = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _staging = Path.Combine(_root, "staging");
            _drive = Path.Combine(_root, "drive");
            _paths = new FakeStoragePathsProvider(Path.Combine(_root, "app"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        /// <summary>
        /// Very compressible content, so a stored entry is easy to tell from a compressed one.
        /// </summary>
        private static string WriteCompressible(string root, string relative, int kilobytes = 64)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string('a', kilobytes * 1024));
            return path;
        }

        private AppSettings Settings() => AppSettings.Default(_drive) with { VortexAppDataPath = Path.Combine(_root, "vortex-appdata") };

        private BackupJobRunner CreateRunner(IArchiveService? archiveService = null)
        {
            var fileSystem = new FileSystemProvider();

            return new BackupJobRunner(
                archiveService ?? new ArchiveService(),
                fileSystem,
                new SnapshotService(fileSystem, new FakeSafetyBackupService()),
                new JsonSnapshotStore(),
                new IgnorePatternMatcher(),
                new FakeJobStateService(),
                _paths);
        }

        private BackupProfile Profile() => new() { GameName = "Skyrim", VortexStagingPath = _staging };

        private static Dictionary<string, (long Length, long Compressed)> ReadEntries(string archivePath)
        {
            using var zip = ZipFile.OpenRead(archivePath);
            return zip.Entries.ToDictionary(entry => entry.FullName, entry => (entry.Length, entry.CompressedLength));
        }

        private static IEnumerable<string> StagingArchives(BackupBatchResult batch, BackupJobRunner _)
        {
            var directory = batch.Results.Single().BackupDirectory;
            return Directory.GetFiles(Path.Combine(Path.GetDirectoryName(directory)!, "_archives"), "*.zip", SearchOption.AllDirectories);
        }

        [Test]
        public async Task ListedFileTypes_AreStored_OthersFollowTheSizeRules()
        {
            var archive = Path.Combine(_root, "out.zip");
            var items = new[]
            {
                new ArchiveFileItem(WriteCompressible(_root, "src/textures.ba2"), "textures.ba2"),
                new ArchiveFileItem(WriteCompressible(_root, "src/music.OGG"), "music.OGG"),
                new ArchiveFileItem(WriteCompressible(_root, "src/texture.dds"), "texture.dds"),
                new ArchiveFileItem(WriteCompressible(_root, "src/noextension"), "noextension")
            };

            await new ArchiveService().CreateArchiveAsync(archive, items, Settings());
            var entries = ReadEntries(archive);

            Assert.Multiple(() =>
            {
                Assert.That(entries["textures.ba2"].Compressed, Is.EqualTo(entries["textures.ba2"].Length), "listed type is stored");
                Assert.That(entries["music.OGG"].Compressed, Is.EqualTo(entries["music.OGG"].Length), "extensions match case-insensitively");
                Assert.That(entries["texture.dds"].Compressed, Is.LessThan(entries["texture.dds"].Length / 10), ".dds shrinks, so it is compressed");
                Assert.That(entries["noextension"].Compressed, Is.LessThan(entries["noextension"].Length / 10));
            });
        }

        [TestCase("ba2")]
        [TestCase("*.BA2")]
        [TestCase(" .Ba2 ")]
        public void ExtensionsAreNormalized(string configured)
        {
            var settings = Settings() with { NoCompressionExtensions = [configured] };

            Assert.Multiple(() =>
            {
                Assert.That(AppSettings.NormalizeExtension(configured), Is.EqualTo(".ba2"));
                Assert.That(settings.IsNoCompressionFile(@"C:\Mods\x\Textures.ba2"), Is.True);
                Assert.That(settings.IsNoCompressionFile(@"C:\Mods\x\Textures.ba2.bak"), Is.False);
                Assert.That(settings.IsNoCompressionFile(@"C:\Mods\x\ba2"), Is.False);
            });
        }

        [Test]
        public void DefaultList_LeavesOutTypesThatShrink()
        {
            var defaults = AppSettings.DefaultNoCompressionExtensions;

            Assert.Multiple(() =>
            {
                Assert.That(defaults, Does.Contain(".ba2").And.Contain(".zip").And.Contain(".7z").And.Contain(".ogg").And.Contain(".bik"));
                Assert.That(defaults, Does.Not.Contain(".dds").And.Not.Contain(".bsa").And.Not.Contain(".esp").And.Not.Contain(".nif"));
                Assert.That(defaults, Is.Unique);
                Assert.That(defaults.All(extension => extension == AppSettings.NormalizeExtension(extension)), Is.True);
            });
        }

        [Test]
        public async Task ComponentAboveTheLimit_IsStoredWithoutCompression_WithANote()
        {
            WriteCompressible(_staging, "ModA/a.txt");
            WriteCompressible(_staging, "ModB/b.txt");
            var settings = Settings() with { StoreComponentsLargerThanBytes = 100 * 1024 };
            var runner = CreateRunner();

            var batch = await runner.RunBackupAsync([Profile()], new BackupOptions(true, false, false, false), settings);
            var entries = StagingArchives(batch, runner).SelectMany(path => ReadEntries(path).Values).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(entries, Has.Count.EqualTo(2));
                Assert.That(entries.All(entry => entry.Compressed == entry.Length), Is.True, "128 KB modpack is above the 100 KB limit");
                Assert.That(batch.Results.Single().Warnings, Has.Some.Contains("stored without compression"));
            });
        }

        [Test]
        public async Task ComponentBelowTheLimit_OrRuleOff_UsesThePerFileRules()
        {
            WriteCompressible(_staging, "ModA/a.txt");
            WriteCompressible(_staging, "ModB/b.txt");
            var runner = CreateRunner();

            var below = await runner.RunBackupAsync([Profile()], new BackupOptions(true, false, false, false), Settings() with { StoreComponentsLargerThanBytes = 1024L * 1024 * 1024 });
            var entries = StagingArchives(below, runner).SelectMany(path => ReadEntries(path).Values).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(entries.All(entry => entry.Compressed < entry.Length / 10), Is.True);
                Assert.That(below.Results.Single().Warnings, Has.None.Contains("stored without compression"));
                Assert.That(Settings().StoreComponentsLargerThanBytes, Is.Zero, "off by default");
            });
        }

        [Test]
        public async Task ManyMods_AreArchivedInParallel_UpToTheSetting_WithTheSameResult()
        {
            for (var mod = 0; mod < 12; mod++)
                WriteCompressible(_staging, $"Mod{mod:00}/file{mod}.txt", kilobytes: 4);

            var restore = new RestoreService(new FakeSafetyBackupService());
            var options = new BackupOptions(true, false, false, false);

            // One archive at a time, into its own destination, as the reference.
            var sequentialCounter = new ConcurrencyCountingArchiveService(new ArchiveService());
            var sequential = await CreateRunner(sequentialCounter).RunBackupAsync(
                [Profile()], options, Settings() with { GoogleDriveBackupPath = Path.Combine(_root, "drive-sequential"), ParallelArchives = 1 });
            var sequentialManifest = (await restore.ReadManifestAsync(sequential.Results.Single().BackupDirectory))!;

            var counting = new ConcurrencyCountingArchiveService(new ArchiveService());
            var runner = CreateRunner(counting);
            var batch = await runner.RunBackupAsync([Profile()], options, Settings() with { ParallelArchives = 4 });
            var manifest = (await restore.ReadManifestAsync(batch.Results.Single().BackupDirectory))!;

            Assert.Multiple(() =>
            {
                Assert.That(sequentialCounter.MaxConcurrent, Is.EqualTo(1));
                Assert.That(counting.MaxConcurrent, Is.InRange(2, 4), "several archives at once, never more than the setting");
                Assert.That(manifest.Units.Select(unit => unit.RelativeDestination), Is.EqualTo(sequentialManifest.Units.Select(unit => unit.RelativeDestination)),
                    "same manifest order as one at a time");
                Assert.That(manifest.Units.Select(unit => unit.FileFingerprints.Single().Sha256), Is.EqualTo(sequentialManifest.Units.Select(unit => unit.FileFingerprints.Single().Sha256)));
                Assert.That(manifest.Units, Has.Count.EqualTo(12));
                Assert.That(batch.ModsArchived, Is.EqualTo(12));
            });

            // The next run reuses every archive written in parallel.
            counting.Reset();
            var second = await runner.RunBackupAsync([Profile()], options, Settings() with { ParallelArchives = 4 });

            Assert.Multiple(() =>
            {
                Assert.That(second.ModsReused, Is.EqualTo(12));
                Assert.That(counting.MaxConcurrent, Is.Zero, "nothing to write");
            });
        }

        [Test]
        public async Task OldSettings_GetTheExtensionListAndTheCoreCount_Once()
        {
            var store = new JsonSettingsStore();
            var path = Path.Combine(_root, "settings.json");
            await File.WriteAllTextAsync(path, """{ "GoogleDriveBackupPath": "D:\\Backups", "VortexAppDataPath": "C:\\Vortex", "DefaultsVersion": 3 }""");

            var migrated = await store.LoadAsync(path);

            Assert.Multiple(() =>
            {
                Assert.That(migrated.NoCompressionExtensions, Is.EqualTo(AppSettings.DefaultNoCompressionExtensions));
                Assert.That(migrated.ParallelArchives, Is.EqualTo(Environment.ProcessorCount));
                Assert.That(migrated.StoreComponentsLargerThanBytes, Is.Zero);
                Assert.That(migrated.VortexAppDataPath, Is.EqualTo(@"C:\Vortex"));
            });

            // The user removes .png and picks 6 archives; neither is changed back.
            await store.SaveAsync(path, migrated with { NoCompressionExtensions = [".ba2"], ParallelArchives = 6 });
            var reloaded = await store.LoadAsync(path);

            Assert.Multiple(() =>
            {
                Assert.That(reloaded.NoCompressionExtensions, Is.EqualTo(new[] { ".ba2" }));
                Assert.That(reloaded.ParallelArchives, Is.EqualTo(6));
            });
        }

        [Test]
        public void ParallelArchives_NotSet_MeansTheCoreCount()
        {
            Assert.Multiple(() =>
            {
                Assert.That((Settings() with { ParallelArchives = 0 }).EffectiveParallelArchives, Is.EqualTo(Environment.ProcessorCount));
                Assert.That((Settings() with { ParallelArchives = 3 }).EffectiveParallelArchives, Is.EqualTo(3));
                Assert.That(AppSettings.Default("x").ParallelArchives, Is.EqualTo(Environment.ProcessorCount));
            });
        }

        /// <summary>
        /// Wraps the real archive service and records how many archives were being written at the same time.
        /// </summary>
        private sealed class ConcurrencyCountingArchiveService(IArchiveService inner) : IArchiveService
        {
            private int _current;
            private int _max;

            public int MaxConcurrent => _max;

            public void Reset() => _max = 0;

            public async Task<ArchiveWriteResult> CreateArchiveAsync(string archivePath, IReadOnlyList<ArchiveFileItem> files, AppSettings settings, IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
            {
                var now = Interlocked.Increment(ref _current);
                InterlockedMax(now);

                try
                {
                    // Long enough that parallel workers overlap.
                    await Task.Delay(50, cancellationToken);
                    return await inner.CreateArchiveAsync(archivePath, files, settings, progress, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _current);
                }
            }

            public Task<ArchiveWriteResult> AppendToArchiveAsync(string archivePath, IReadOnlyList<ArchiveFileItem> files, AppSettings settings, IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
                => inner.AppendToArchiveAsync(archivePath, files, settings, progress, cancellationToken);

            private void InterlockedMax(int value)
            {
                int initial;

                do
                {
                    initial = _max;

                    if (value <= initial)
                        return;
                }
                while (Interlocked.CompareExchange(ref _max, value, initial) != initial);
            }
        }
    }
}
