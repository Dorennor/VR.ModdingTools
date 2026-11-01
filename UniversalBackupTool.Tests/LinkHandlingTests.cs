using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.FileSystem;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Settings;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Services;
using UniversalBackupTool.Services.Backups;

namespace UniversalBackupTool.Tests
{
    /// <summary>
    /// Real hardlinks, symlinks and junctions. Symlink tests are skipped when the machine does not allow creating them
    /// (Developer Mode off and not elevated); hardlinks and junctions need no privileges.
    /// </summary>
    [TestFixture]
    public class LinkHandlingTests
    {
        private string _root = string.Empty;
        private string _game = string.Empty;
        private string _staging = string.Empty;
        private string _outside = string.Empty;
        private FileSystemProvider _fileSystem = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _game = Path.Combine(_root, "game");
            _staging = Path.Combine(_root, "staging");
            _outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(_game);
            Directory.CreateDirectory(_staging);
            Directory.CreateDirectory(_outside);
            _fileSystem = new FileSystemProvider();
        }

        [TearDown]
        public void TearDown()
        {
            // Remove links first so the recursive delete never walks into link targets.
            foreach (var entry in Directory.Exists(_root) ? _fileSystem.ScanWithoutFollowingLinks(_root) : [])
            {
                if (entry.Kind is FileEntryKind.DirectoryLink or FileEntryKind.FileSymlink)
                    _fileSystem.DeleteEntry(entry);
            }

            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static string Write(string path, string content)
        {
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

        private static void Junction(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            process.WaitForExit();

            if (process.ExitCode != 0 || !Directory.Exists(link))
                Assert.Ignore("Junctions could not be created here.");
        }

        private static void FileSymlink(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);

            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Assert.Ignore("Symbolic links need Developer Mode or elevation on this machine.");
            }
        }

        [Test]
        public void ScanWithoutFollowingLinks_ClassifiesEntries_AndNeverEntersDirectoryLinks()
        {
            var stagingFile = Write(Path.Combine(_staging, "mod", "plugin.esp"), "mod data");
            Write(Path.Combine(_game, "game.exe"), "vanilla");
            HardLink(Path.Combine(_game, "Data", "plugin.esp"), stagingFile);
            Junction(Path.Combine(_game, "LinkedDir"), _outside);
            Write(Path.Combine(_outside, "inside-target.txt"), "outside");
            FileSymlink(Path.Combine(_game, "linked.ini"), stagingFile);

            var entries = _fileSystem.ScanWithoutFollowingLinks(_game).ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);

            Assert.Multiple(() =>
            {
                Assert.That(entries["game.exe"].Kind, Is.EqualTo(FileEntryKind.File));
                Assert.That(entries["Data/plugin.esp"].Kind, Is.EqualTo(FileEntryKind.HardLink));
                Assert.That(entries["Data/plugin.esp"].LinkCount, Is.EqualTo(2));
                Assert.That(entries["LinkedDir"].Kind, Is.EqualTo(FileEntryKind.DirectoryLink));
                Assert.That(entries["linked.ini"].Kind, Is.EqualTo(FileEntryKind.FileSymlink));
                Assert.That(entries.Keys, Has.None.StartsWith("LinkedDir/"), "files behind a directory link must not be listed");
            });
        }

        [Test]
        public async Task Purge_RemovesUnregisteredLinks_WithoutTouchingTheirTargets()
        {
            var stagingFile = Write(Path.Combine(_staging, "mod", "plugin.esp"), "mod data");
            var outsideFile = Write(Path.Combine(_outside, "keep.txt"), "outside");
            var symlinkTarget = Write(Path.Combine(_staging, "mod", "settings.ini"), "ini");
            Write(Path.Combine(_game, "game.exe"), "vanilla");

            var snapshotService = new SnapshotService(_fileSystem, new FakeSafetyBackupService());
            var baseline = await snapshotService.CreateSnapshotAsync(Guid.NewGuid(), _game);

            HardLink(Path.Combine(_game, "Data", "plugin.esp"), stagingFile);
            Junction(Path.Combine(_game, "LinkedDir"), _outside);
            FileSymlink(Path.Combine(_game, "settings.ini"), symlinkTarget);
            Write(Path.Combine(_game, "generated.log"), "new file");

            var safety = new FakeSafetyBackupService();
            var result = await new SnapshotService(_fileSystem, safety).PurgeUnregisteredFilesAsync(_game, baseline);

            Assert.Multiple(() =>
            {
                Assert.That(result.FilesDeleted, Is.EqualTo(4));
                Assert.That(result.LinksRemoved, Is.EqualTo(3));
                Assert.That(Directory.EnumerateFileSystemEntries(_game, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(_game, path)), Is.EquivalentTo(new[] { "game.exe" }), "Data was created after the snapshot for the hardlink, so it is removed once empty");
                Assert.That(result.FoldersRemoved, Is.EqualTo(1));

                // Targets are untouched.
                Assert.That(File.ReadAllText(stagingFile), Is.EqualTo("mod data"));
                Assert.That(File.ReadAllText(outsideFile), Is.EqualTo("outside"));
                Assert.That(File.ReadAllText(symlinkTarget), Is.EqualTo("ini"));

                // Only the real file needed a safety copy; link data still exists elsewhere.
                Assert.That(safety.LastFiles.Select(file => file.EntryName), Is.EquivalentTo(new[] { "generated.log" }));
            });
        }

        [Test]
        public async Task Purge_CopiesHardlinkedFile_WhenEveryNameIsDeleted()
        {
            Write(Path.Combine(_game, "game.exe"), "vanilla");
            var snapshotService = new SnapshotService(_fileSystem, new FakeSafetyBackupService());
            var baseline = await snapshotService.CreateSnapshotAsync(Guid.NewGuid(), _game);

            var first = Write(Path.Combine(_game, "a.dat"), "only copy");
            HardLink(Path.Combine(_game, "b.dat"), first);

            var safety = new FakeSafetyBackupService();
            await new SnapshotService(_fileSystem, safety).PurgeUnregisteredFilesAsync(_game, baseline);

            Assert.That(safety.LastFiles, Has.Count.EqualTo(1), "both names were deleted, so one copy must be kept");
        }

        [Test]
        public async Task GetChanges_ReportsLinkKinds_SoConfigBackupCanSkipThem()
        {
            Write(Path.Combine(_game, "game.exe"), "vanilla");
            var snapshotService = new SnapshotService(_fileSystem, new FakeSafetyBackupService());
            var baseline = await snapshotService.CreateSnapshotAsync(Guid.NewGuid(), _game);

            HardLink(Path.Combine(_game, "Data", "plugin.esp"), Write(Path.Combine(_staging, "mod", "plugin.esp"), "mod"));
            Write(Path.Combine(_game, "user.ini"), "config");

            var changes = await snapshotService.GetChangesAsync(_game, baseline);

            Assert.Multiple(() =>
            {
                Assert.That(changes["Data/plugin.esp"].IsLink, Is.True);
                Assert.That(changes["user.ini"].IsLink, Is.False);
            });
        }

        [Test]
        public void ScanFollowingLinks_IncludesLinkTargets_AndSkipsLoops()
        {
            Write(Path.Combine(_staging, "mod", "a.txt"), "A");
            Write(Path.Combine(_outside, "b.txt"), "B");
            Junction(Path.Combine(_staging, "linked"), _outside);
            Junction(Path.Combine(_staging, "mod", "loop"), _staging);

            var scan = _fileSystem.ScanFollowingLinks(_staging);

            Assert.Multiple(() =>
            {
                Assert.That(scan.Files.Select(file => file.RelativePath), Is.EquivalentTo(new[] { "mod/a.txt", "linked/b.txt" }));
                Assert.That(scan.LinksFollowed, Is.EqualTo(1));
                Assert.That(scan.Warnings, Has.Some.Contains("already included"));
            });
        }

        [Test]
        public void ScanFollowingLinks_ReportsBrokenFileLinks()
        {
            var target = Write(Path.Combine(_outside, "gone.txt"), "x");
            FileSymlink(Path.Combine(_staging, "broken.txt"), target);
            File.Delete(target);

            var scan = _fileSystem.ScanFollowingLinks(_staging);

            Assert.Multiple(() =>
            {
                Assert.That(scan.Files, Is.Empty);
                Assert.That(scan.Warnings, Has.Some.Contains("broken"));
            });
        }

        [Test]
        public async Task Backup_CustomPaths_ArchivesSymlinkTargetBytes_SingleFiles_AndRestoresThem()
        {
            var appRoot = Path.Combine(_root, "app");
            var drive = Path.Combine(_root, "drive");
            var custom = Path.Combine(_root, "custom");
            var realIni = Write(Path.Combine(_outside, "real.ini"), "real content");
            FileSymlink(Path.Combine(custom, "AddOns", "linked.ini"), realIni);
            Write(Path.Combine(custom, "AddOns", "addon.lua"), "lua");
            var singleFile = Write(Path.Combine(custom, "Prefs", "UserSettings.txt"), "settings v1");
            Write(Path.Combine(custom, "Prefs", "NotIncluded.txt"), "other file in the same folder");

            var paths = new FakeStoragePathsProvider(appRoot);
            var runner = new BackupJobRunner(
                new ArchiveService(),
                _fileSystem,
                new SnapshotService(_fileSystem, new FakeSafetyBackupService()),
                new JsonSnapshotStore(),
                new IgnorePatternMatcher(),
                new FakeJobStateService(),
                paths);
            var restore = new RestoreService(new SafetyBackupService(paths, new JsonSettingsStore()));
            var settings = AppSettings.Default(drive);
            var profile = new BackupProfile
            {
                GameName = "Links",
                CustomFolders = [Path.Combine(custom, "AddOns"), singleFile]
            };
            var options = new BackupOptions(false, false, false, true);

            var batch = await runner.RunBackupAsync([profile], options, settings);
            var directory = batch.Results.Single().BackupDirectory;
            var manifest = (await restore.ReadManifestAsync(directory))!;

            var fileUnit = manifest.Units.Single(unit => unit.CustomPath == singleFile);
            using (var zip = ZipFile.OpenRead(Path.Combine(directory, fileUnit.ArchiveRootRelativePath, fileUnit.ArchiveFile)))
                Assert.That(zip.Entries.Select(entry => entry.FullName), Is.EqualTo(new[] { "UserSettings.txt" }));

            var folderUnit = manifest.Units.Single(unit => unit.CustomPath == Path.Combine(custom, "AddOns"));
            using (var zip = ZipFile.OpenRead(Path.Combine(directory, folderUnit.ArchiveRootRelativePath, folderUnit.ArchiveFile)))
            using (var reader = new StreamReader(zip.GetEntry("linked.ini")!.Open()))
                Assert.That(reader.ReadToEnd(), Is.EqualTo("real content"), "the symlink's target bytes must be archived");

            // Restore puts the single file back in place and leaves its folder neighbours alone.
            File.WriteAllText(singleFile, "changed");
            var plan = restore.BuildPlan(manifest, [profile], [BackupComponent.CustomFolders]);
            await restore.RestoreAsync(directory, plan);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(singleFile), Is.EqualTo("settings v1"));
                Assert.That(File.ReadAllText(Path.Combine(custom, "Prefs", "NotIncluded.txt")), Is.EqualTo("other file in the same folder"));
                Assert.That(plan.Single(item => item.Unit.CustomPath == singleFile).DestinationPath, Is.EqualTo(Path.Combine(custom, "Prefs")));
            });
        }

        [Test]
        public async Task Backup_SymlinkTargetChange_IsDetectedOnNextRun()
        {
            var appRoot = Path.Combine(_root, "app");
            var custom = Path.Combine(_root, "custom");
            var realIni = Write(Path.Combine(_outside, "real.ini"), "v1");
            FileSymlink(Path.Combine(custom, "linked.ini"), realIni);

            var runner = new BackupJobRunner(
                new ArchiveService(),
                _fileSystem,
                new SnapshotService(_fileSystem, new FakeSafetyBackupService()),
                new JsonSnapshotStore(),
                new IgnorePatternMatcher(),
                new FakeJobStateService(),
                new FakeStoragePathsProvider(appRoot));
            var settings = AppSettings.Default(Path.Combine(_root, "drive"));
            var profile = new BackupProfile { GameName = "Links", CustomFolders = [custom] };
            var options = new BackupOptions(false, false, false, true);

            await runner.RunBackupAsync([profile], options, settings);
            File.WriteAllText(realIni, "version 2 is longer");
            var second = await runner.RunBackupAsync([profile], options, settings);

            Assert.That(second.FilesArchived, Is.EqualTo(1), "editing the link target must re-archive the unit");
        }

        [Test]
        public void DeleteEntry_DirectoryLink_RemovesOnlyTheLink()
        {
            var keep = Write(Path.Combine(_outside, "deep", "keep.txt"), "keep");
            var link = Path.Combine(_game, "LinkedDir");
            Junction(link, _outside);

            var entry = _fileSystem.ScanWithoutFollowingLinks(_game).Single();
            _fileSystem.DeleteEntry(entry);

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(link), Is.False);
                Assert.That(File.ReadAllText(keep), Is.EqualTo("keep"));
            });
        }
    }
}
