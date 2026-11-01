using System.Runtime.InteropServices;

using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Services;
using UniversalBackupTool.Services.Backups;

namespace UniversalBackupTool.Tests
{
    /// <summary>
    /// Purge after a game update: the snapshot no longer matches, so the files of the game's own backups are removed,
    /// but only while their content still matches the backup.
    /// </summary>
    [TestFixture]
    public class TrackedPurgeTests
    {
        private string _root = string.Empty;
        private string _game = string.Empty;
        private string _staging = string.Empty;
        private string _drive = string.Empty;
        private FakeStoragePathsProvider _paths = null!;
        private FileSystemProvider _fileSystem = null!;
        private FakeSafetyBackupService _safety = null!;
        private BackupJobRunner _runner = null!;
        private TrackedFilesPurgeService _purge = null!;
        private AppSettings _settings = null!;
        private BackupProfile _profile = null!;
        private SnapshotData _baseline = null!;

        [SetUp]
        public async Task SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _game = Path.Combine(_root, "game");
            _staging = Path.Combine(_root, "staging");
            _drive = Path.Combine(_root, "drive");
            _paths = new FakeStoragePathsProvider(Path.Combine(_root, "app"));
            _fileSystem = new FileSystemProvider();
            _safety = new FakeSafetyBackupService();
            _runner = new BackupJobRunner(
                new ArchiveService(),
                _fileSystem,
                new SnapshotService(_fileSystem, new FakeSafetyBackupService()),
                new JsonSnapshotStore(),
                new IgnorePatternMatcher(),
                new FakeJobStateService(),
                _paths);
            _purge = new TrackedFilesPurgeService(_fileSystem, _safety);
            _settings = AppSettings.Default(_drive) with { VortexAppDataPath = Path.Combine(_root, "vortex-appdata") };
            _profile = new BackupProfile { GameName = "Witcher", GameRootPath = _game, VortexStagingPath = _staging };

            // Vanilla game, then its snapshot.
            Write(_game, "game.exe", "v1");
            Write(_game, "Data/base.pak", "vanilla");
            Directory.CreateDirectory(Path.Combine(_game, "Saves"));

            _baseline = await new SnapshotService(_fileSystem, new FakeSafetyBackupService()).CreateSnapshotAsync(_profile.Id, _game);
            await new JsonSnapshotStore().SaveAsync(_paths.GetPaths().SnapshotsDirectory, _baseline);

            // Modding: new files and folders, a config, an overwritten original file and a Vortex hardlink.
            Write(_game, "Mods/MyMod/script.lua", "mod script");
            Write(_game, "Mods/MyMod/Sub/data.bin", "mod data");
            Write(_game, "Data/mod.pak", "mod pak");
            Write(_game, "user.ini", "config");
            Write(_game, "Data/base.pak", "modded vanilla");
            HardLink(Path.Combine(_game, "Data/vortex.pak"), Write(_staging, "SomeMod/vortex.pak", "vortex deployed"));

            await _runner.RunBackupAsync([_profile], new BackupOptions(false, true, false, false, IncludeGameChanges: true), _settings);

            // The game updates: it replaces an original file and a file a mod had added, and writes a log.
            Write(_game, "game.exe", "v2");
            Write(_game, "Data/mod.pak", "updated by the game");
            Write(_game, "Logs/new.log", "written after the backup");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static string Write(string root, string relative, string content)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

        private static void HardLink(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);

            if (!CreateHardLinkW(link, target, IntPtr.Zero))
                Assert.Ignore($"Hardlinks are not supported here (error {Marshal.GetLastWin32Error()}).");
        }

        private Task<TrackedPurgePlan> PlanAsync() => _purge.BuildPlanAsync(_profile, _game, _baseline, _drive);

        private static TrackedFileStatus StatusOf(TrackedPurgePlan plan, string path) => plan.Files.Single(file => file.RelativePath == path).Status;

        [Test]
        public async Task Plan_RemovesOnlyUnchangedFilesOfTheBackups_AndKeepsTheRest()
        {
            var plan = await PlanAsync();

            Assert.Multiple(() =>
            {
                Assert.That(plan.Backups, Has.Count.EqualTo(2), "newest Changed game files and Game configs backups");
                Assert.That(StatusOf(plan, "Mods/MyMod/script.lua"), Is.EqualTo(TrackedFileStatus.Remove));
                Assert.That(StatusOf(plan, "Mods/MyMod/Sub/data.bin"), Is.EqualTo(TrackedFileStatus.Remove));
                Assert.That(StatusOf(plan, "user.ini"), Is.EqualTo(TrackedFileStatus.Remove));
                Assert.That(StatusOf(plan, "Data/mod.pak"), Is.EqualTo(TrackedFileStatus.ChangedSinceBackup));
                Assert.That(StatusOf(plan, "Data/base.pak"), Is.EqualTo(TrackedFileStatus.OriginalGameFile));
                Assert.That(StatusOf(plan, "Data/vortex.pak"), Is.EqualTo(TrackedFileStatus.VortexLink));
                Assert.That(plan.Files.Select(file => file.RelativePath), Does.Not.Contain("game.exe"), "not in any backup");
                Assert.That(plan.Files.Select(file => file.RelativePath), Does.Not.Contain("Logs/new.log"), "created after the backup");
                Assert.That(plan.Folders, Is.EqualTo(new[] { "Mods/MyMod/Sub", "Mods/MyMod", "Mods" }), "deepest first, vanilla folders excluded");
                Assert.That(plan.FilesAlreadyGone, Is.Zero);
            });
        }

        [Test]
        public async Task Purge_DeletesThePlannedFilesAndEmptyModFolders_WithASafetyBackup()
        {
            var plan = await PlanAsync();
            var result = await _purge.PurgeAsync(plan);

            Assert.Multiple(() =>
            {
                Assert.That(result.FilesDeleted, Is.EqualTo(3));
                Assert.That(result.FoldersRemoved, Is.EqualTo(3));
                Assert.That(result.FilesKept, Is.Empty);
                Assert.That(_safety.LastFiles.Select(file => file.EntryName), Is.EquivalentTo(new[] { "Mods/MyMod/script.lua", "Mods/MyMod/Sub/data.bin", "user.ini" }));
                Assert.That(_safety.LastCategory, Is.EqualTo("Purge"));
                Assert.That(Directory.Exists(Path.Combine(_game, "Mods")), Is.False);
                Assert.That(File.Exists(Path.Combine(_game, "user.ini")), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(_game, "Data", "mod.pak")), Is.EqualTo("updated by the game"));
                Assert.That(File.ReadAllText(Path.Combine(_game, "Data", "base.pak")), Is.EqualTo("modded vanilla"));
                Assert.That(File.Exists(Path.Combine(_game, "Data", "vortex.pak")), Is.True, "Vortex links are left to Vortex");
                Assert.That(File.Exists(Path.Combine(_staging, "SomeMod", "vortex.pak")), Is.True);
                Assert.That(File.ReadAllText(Path.Combine(_game, "game.exe")), Is.EqualTo("v2"));
                Assert.That(File.Exists(Path.Combine(_game, "Logs", "new.log")), Is.True);
                Assert.That(Directory.Exists(Path.Combine(_game, "Saves")), Is.True, "empty vanilla folders stay");
            });
        }

        [Test]
        public async Task Purge_KeepsAFileThatChangedAfterThePreview_AndItsFolder()
        {
            var plan = await PlanAsync();
            var script = Path.Combine(_game, "Mods", "MyMod", "script.lua");
            File.WriteAllText(script, "edited after the preview, longer than before");

            var result = await _purge.PurgeAsync(plan);

            Assert.Multiple(() =>
            {
                Assert.That(result.FilesKept, Is.EqualTo(new[] { "Mods/MyMod/script.lua" }));
                Assert.That(File.Exists(script), Is.True);
                Assert.That(Directory.Exists(Path.Combine(_game, "Mods", "MyMod", "Sub")), Is.False);
                Assert.That(result.FoldersKept, Is.EqualTo(2), "Mods/MyMod and Mods still hold the kept file");
            });
        }

        [Test]
        public async Task Plan_UsesTheNewestCompleteBackup()
        {
            // Newer backups of both components without the mod folder: their files are what counts now.
            Directory.Delete(Path.Combine(_game, "Mods"), recursive: true);
            await _runner.RunBackupAsync([_profile], new BackupOptions(false, true, false, false, IncludeGameChanges: true), _settings);
            Write(_game, "Mods/MyMod/script.lua", "mod script");

            var plan = await PlanAsync();

            Assert.Multiple(() =>
            {
                Assert.That(plan.Backups.SelectMany(backup => backup.Manifest.Units).SelectMany(unit => unit.FileFingerprints).Select(file => file.RelativePath),
                    Does.Not.Contain("Mods/MyMod/script.lua"));
                Assert.That(plan.Files.Select(file => file.RelativePath), Does.Not.Contain("Mods/MyMod/script.lua"));
                Assert.That(StatusOf(plan, "Logs/new.log"), Is.EqualTo(TrackedFileStatus.Remove), "in the newer backups");
                Assert.That(StatusOf(plan, "game.exe"), Is.EqualTo(TrackedFileStatus.OriginalGameFile));
            });
        }

        [Test]
        public async Task Plan_WithoutBackups_HasNothingToDo()
        {
            var other = _profile with { Id = Guid.NewGuid(), GameName = "Other Game" };

            var plan = await _purge.BuildPlanAsync(other, _game, _baseline, _drive);

            Assert.Multiple(() =>
            {
                Assert.That(plan.HasBackups, Is.False);
                Assert.That(plan.Files, Is.Empty);
                Assert.That(plan.Folders, Is.Empty);
            });
        }

        [Test]
        public async Task Plan_ReportsFilesAlreadyGone()
        {
            File.Delete(Path.Combine(_game, "user.ini"));

            var plan = await PlanAsync();

            Assert.Multiple(() =>
            {
                Assert.That(plan.FilesAlreadyGone, Is.EqualTo(1));
                Assert.That(plan.Files.Select(file => file.RelativePath), Does.Not.Contain("user.ini"));
            });
        }
    }
}
