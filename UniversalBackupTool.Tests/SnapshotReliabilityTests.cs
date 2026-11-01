using System.Runtime.InteropServices;

using UniversalBackupTool.Model.Models.FileSystem;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Services;

namespace UniversalBackupTool.Tests
{
    /// <summary>
    /// Timestamps are only the first pass; size, version and hash decide. A reinstalled, copied or moved game therefore still
    /// matches its snapshot, and existing snapshots can be upgraded without ever taking data from a file that is not vanilla.
    /// </summary>
    [TestFixture]
    public class SnapshotReliabilityTests
    {
        private string _root = string.Empty;
        private string _game = string.Empty;
        private SnapshotService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _game = Path.Combine(_root, "game");
            _service = new SnapshotService(new FileSystemProvider(), new FakeSafetyBackupService());

            Write("game.exe", "vanilla exe");
            Write("Data/main.bsa", "vanilla archive");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private string Full(string relative, string? root = null) => Path.Combine(root ?? _game, relative.Replace('/', Path.DirectorySeparatorChar));

        private string Write(string relative, string content)
        {
            var path = Full(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>
        /// Same bytes in a new file with other times: what a reinstall of the same version leaves behind.
        /// </summary>
        private void Reinstall(string relative)
        {
            var path = Full(relative);
            var content = File.ReadAllText(path);
            File.Delete(path);
            File.WriteAllText(path, content);
            File.SetCreationTimeUtc(path, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc));
        }

        /// <summary>
        /// A snapshot as written before creation times and versions existed.
        /// </summary>
        private static SnapshotData Legacy(SnapshotData snapshot, bool keepHashes = true) => snapshot with
        {
            Files = snapshot.Files.ToDictionary(
                pair => pair.Key,
                pair => pair.Value with { CreationTimeUtc = null, FileVersion = null, Sha256 = keepHashes ? pair.Value.Sha256 : null },
                StringComparer.OrdinalIgnoreCase)
        };

        private async Task<(SnapshotUpgradeResult Result, Dictionary<string, FileMetadata> Entries)> UpgradeAsync(SnapshotData snapshot)
        {
            var entries = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);
            var result = await _service.UpgradeSnapshotAsync(_game, snapshot, onUpgraded: entry => entries[entry.RelativePath] = entry);
            return (result, entries);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

        [Test]
        public async Task Compare_Reinstall_WithAllTimesChanged_ReportsNothing_AndAnchorsTheNewTimes()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Reinstall("game.exe");

            var comparison = await _service.CompareAsync(_game, baseline, verifyContent: true);
            var anchored = comparison.Reanchored.Single();

            Assert.Multiple(() =>
            {
                Assert.That(comparison.Changes, Is.Empty);
                Assert.That(anchored.LastWriteTimeUtc, Is.EqualTo(File.GetLastWriteTimeUtc(Full("game.exe"))));
                Assert.That(anchored.CreationTimeUtc, Is.EqualTo(File.GetCreationTimeUtc(Full("game.exe"))));
                Assert.That(anchored.Sha256, Is.EqualTo(baseline.Files["game.exe"].Sha256));
            });
        }

        [Test]
        public async Task Compare_OnlyCreationTimeChanged_IsCheckedByContent()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            var path = Full("game.exe");
            var modified = File.GetLastWriteTimeUtc(path);
            File.SetCreationTimeUtc(path, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(path, modified);

            var comparison = await _service.CompareAsync(_game, baseline, verifyContent: true);

            Assert.Multiple(() =>
            {
                Assert.That(comparison.Changes, Is.Empty);
                Assert.That(comparison.Reanchored.Single().RelativePath, Is.EqualTo("game.exe"));
            });
        }

        [Test]
        public async Task Compare_AnchoredSnapshot_NeedsNoSecondCheck()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Reinstall("game.exe");

            var first = await _service.CompareAsync(_game, baseline, verifyContent: true);
            var files = new Dictionary<string, FileMetadata>(baseline.Files, StringComparer.OrdinalIgnoreCase) { ["game.exe"] = first.Reanchored.Single() };
            var second = await _service.CompareAsync(_game, baseline with { Files = files }, verifyContent: true);

            Assert.That(second.Reanchored, Is.Empty);
        }

        [Test]
        public async Task Compare_GameFolderCopiedElsewhere_StillMatchesTheSnapshot()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            var copy = Path.Combine(_root, "other drive", "game");

            foreach (var relative in baseline.Files.Keys)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Full(relative, copy))!);
                File.Copy(Full(relative), Full(relative, copy));
            }

            var comparison = await _service.CompareAsync(copy, baseline, verifyContent: true);

            Assert.That(comparison.Changes, Is.Empty);
        }

        [Test]
        public async Task Compare_SameSizeOtherContent_IsChanged_WhateverTheTimes()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Write("game.exe", "patched exe");
            File.SetLastWriteTimeUtc(Full("game.exe"), new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc));

            var comparison = await _service.CompareAsync(_game, baseline, verifyContent: true);

            Assert.That(comparison.Changes.Keys, Is.EquivalentTo(new[] { "game.exe" }));
        }

        [Test]
        public async Task Compare_UnreadableFile_IsChanged_AndCounted()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Reinstall("game.exe");

            using var locked = new FileStream(Full("game.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
            var comparison = await _service.CompareAsync(_game, baseline, verifyContent: true);

            Assert.Multiple(() =>
            {
                Assert.That(comparison.Changes.Keys, Is.EquivalentTo(new[] { "game.exe" }));
                Assert.That(comparison.UnreadableFiles, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task Compare_HardlinkWithVanillaContent_IsTheVanillaFile()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Assert.That(CreateHardLink(Path.Combine(_root, "other-name.bin"), Full("game.exe"), IntPtr.Zero), Is.True);

            var comparison = await _service.CompareAsync(_game, baseline, verifyContent: true);

            Assert.Multiple(() =>
            {
                Assert.That(comparison.Changes, Is.Empty);
                Assert.That(comparison.Reanchored.Single().Kind, Is.EqualTo(FileEntryKind.HardLink));
                Assert.That(comparison.Reanchored.Single().Sha256, Is.Not.Null);
            });
        }

        [Test]
        public async Task Compare_HardlinkWithOtherContent_IsChanged()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            var staged = Path.Combine(_root, "staged.bin");
            File.WriteAllText(staged, "modded  exe");
            File.Delete(Full("game.exe"));
            Assert.That(CreateHardLink(Full("game.exe"), staged, IntPtr.Zero), Is.True);

            var comparison = await _service.CompareAsync(_game, baseline, verifyContent: true);

            Assert.That(comparison.Changes["game.exe"].Kind, Is.EqualTo(FileEntryKind.HardLink));
        }

        [Test]
        public async Task CreateSnapshot_RecordsVersionOfLibraries_AndCreationTimes()
        {
            File.Copy(typeof(SnapshotService).Assembly.Location, Full("library.dll"));

            var snapshot = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Files["library.dll"].FileVersion, Is.Not.Null.And.Not.Empty);
                Assert.That(snapshot.Files["Data/main.bsa"].FileVersion, Is.Null);
                Assert.That(snapshot.Files["game.exe"].CreationTimeUtc, Is.EqualTo(File.GetCreationTimeUtc(Full("game.exe"))));
            });
        }

        [Test]
        public async Task Compare_OtherFileVersion_IsChanged_AndCounted()
        {
            File.Copy(typeof(SnapshotService).Assembly.Location, Full("library.dll"));
            var snapshot = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            var files = new Dictionary<string, FileMetadata>(snapshot.Files, StringComparer.OrdinalIgnoreCase);
            files["library.dll"] = files["library.dll"] with { FileVersion = "0.0.0.1", LastWriteTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

            var comparison = await _service.CompareAsync(_game, snapshot with { Files = files }, verifyContent: true);

            Assert.Multiple(() =>
            {
                Assert.That(comparison.VersionChanged, Is.EqualTo(1));
                Assert.That(comparison.Changes.Keys, Is.EquivalentTo(new[] { "library.dll" }));
                Assert.That(comparison.Changes["library.dll"].FileVersion, Is.Not.EqualTo("0.0.0.1"));
            });
        }

        [Test]
        public async Task Compare_FindsMovedAndCopiedVanillaFiles_AndKeepsThemInTheChanges()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Directory.CreateDirectory(Full("Moved"));
            File.Move(Full("Data/main.bsa"), Full("Moved/main.bsa"));
            File.Copy(Full("game.exe"), Full("game_backup.exe"));
            Write("sameSize.txt", "other bytes"); // the size of game.exe, other content

            var comparison = await _service.CompareAsync(_game, baseline, verifyContent: true);

            Assert.Multiple(() =>
            {
                Assert.That(comparison.Relocated, Is.EquivalentTo(new[]
                {
                    new RelocatedFile("Moved/main.bsa", "Data/main.bsa", true),
                    new RelocatedFile("game_backup.exe", "game.exe", false)
                }));
                Assert.That(comparison.Changes.ContainsKey("Moved/main.bsa"), Is.True, "a backup needs the new path");
                Assert.That(comparison.Changes.ContainsKey("game_backup.exe"), Is.True, "a backup needs the new path");
            });
        }

        [Test]
        public async Task Relocation_OneVanillaFile_IsMovedOnce_OtherCopiesAreCopies()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            File.Copy(Full("game.exe"), Full("a.exe"));
            File.Move(Full("game.exe"), Full("b.exe"));

            var relocated = await _service.FindRelocatedFilesAsync(_game, baseline);

            Assert.That(relocated, Is.EqualTo(new[]
            {
                new RelocatedFile("a.exe", "game.exe", true),
                new RelocatedFile("b.exe", "game.exe", false)
            }));
        }

        [Test]
        public async Task Relocation_IgnoresEmptyFiles()
        {
            Write("empty.txt", string.Empty);
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Write("other-empty.txt", string.Empty);

            Assert.That(await _service.FindRelocatedFilesAsync(_game, baseline), Is.Empty);
        }

        [Test]
        public async Task Purge_PutsMovedVanillaFileBack_DeletesCopiesAndOtherFiles()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            var modified = File.GetLastWriteTimeUtc(Full("Data/main.bsa"));
            Directory.CreateDirectory(Full("Moved"));
            File.Move(Full("Data/main.bsa"), Full("Moved/main.bsa"));
            Directory.Delete(Full("Data"));
            File.Copy(Full("game.exe"), Full("game_backup.exe"));
            Write("mod.esp", "mod");

            var result = await _service.PurgeUnregisteredFilesAsync(_game, baseline);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Full("Data/main.bsa")), Is.EqualTo("vanilla archive"));
                Assert.That(File.GetLastWriteTimeUtc(Full("Data/main.bsa")), Is.EqualTo(modified));
                Assert.That(Directory.Exists(Full("Moved")), Is.False, "the emptied folder is not vanilla");
                Assert.That(File.Exists(Full("game_backup.exe")), Is.False);
                Assert.That(File.Exists(Full("mod.esp")), Is.False);
                Assert.That(File.Exists(Full("game.exe")), Is.True);
                Assert.That(result.FilesMovedBack, Is.EqualTo(1));
                Assert.That(result.FilesDeleted, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task Upgrade_AddsMissingDetails_AndKeepsTheHash()
        {
            File.Copy(typeof(SnapshotService).Assembly.Location, Full("library.dll"));
            var complete = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);

            var (result, entries) = await UpgradeAsync(Legacy(complete));

            Assert.Multiple(() =>
            {
                Assert.That(result.Upgraded, Is.EqualTo(3));
                Assert.That(entries["library.dll"].FileVersion, Is.EqualTo(complete.Files["library.dll"].FileVersion));
                Assert.That(entries["game.exe"].CreationTimeUtc, Is.EqualTo(complete.Files["game.exe"].CreationTimeUtc));
                Assert.That(entries["game.exe"].Sha256, Is.EqualTo(complete.Files["game.exe"].Sha256));
            });
        }

        [Test]
        public async Task Upgrade_ReinstalledFile_IsProvenByItsHash()
        {
            var legacy = Legacy(await _service.CreateSnapshotAsync(Guid.NewGuid(), _game));
            Reinstall("game.exe");

            var (result, entries) = await UpgradeAsync(legacy);

            Assert.Multiple(() =>
            {
                Assert.That(result.Different, Is.Empty);
                Assert.That(entries["game.exe"].LastWriteTimeUtc, Is.EqualTo(File.GetLastWriteTimeUtc(Full("game.exe"))));
                Assert.That(entries["game.exe"].Sha256, Is.EqualTo(legacy.Files["game.exe"].Sha256));
            });
        }

        [Test]
        public async Task Upgrade_ModifiedFiles_NeverEnterTheBaseline()
        {
            var legacy = Legacy(await _service.CreateSnapshotAsync(Guid.NewGuid(), _game));
            Write("game.exe", "patched exe"); // same size, other content
            Write("Data/main.bsa", "a patched and longer archive"); // other size

            var (result, entries) = await UpgradeAsync(legacy);

            Assert.Multiple(() =>
            {
                Assert.That(result.Different, Is.EquivalentTo(new[] { "game.exe", "Data/main.bsa" }));
                Assert.That(entries, Is.Empty);
            });
        }

        [Test]
        public async Task Upgrade_NoStoredHash_TrustsOnlyMatchingMetadata()
        {
            Write("same.txt", "same");
            var legacy = Legacy(await _service.CreateSnapshotAsync(Guid.NewGuid(), _game), keepHashes: false);
            Reinstall("game.exe");
            Directory.Delete(Full("Data"), recursive: true);

            var (result, entries) = await UpgradeAsync(legacy);

            Assert.Multiple(() =>
            {
                Assert.That(result.CannotProve, Is.EqualTo(1));
                Assert.That(result.Missing, Is.EqualTo(1));
                Assert.That(entries.Keys, Is.EquivalentTo(new[] { "same.txt" }));
                Assert.That(entries["same.txt"].Sha256, Has.Length.EqualTo(64));
            });
        }

        [Test]
        public async Task Upgrade_DeployedLink_GetsNoDetails_WithoutAHash()
        {
            var legacy = Legacy(await _service.CreateSnapshotAsync(Guid.NewGuid(), _game), keepHashes: false);
            var staged = Path.Combine(_root, "staged.bin");
            File.WriteAllText(staged, "modded exe!");
            File.Delete(Full("game.exe"));
            Assert.That(CreateHardLink(Full("game.exe"), staged, IntPtr.Zero), Is.True);

            var (result, entries) = await UpgradeAsync(legacy);

            Assert.Multiple(() =>
            {
                Assert.That(result.Links, Is.EqualTo(1));
                Assert.That(entries.ContainsKey("game.exe"), Is.False);
            });
        }

        [Test]
        public async Task Upgrade_LockedFile_WithStoredHash_IsLeftAlone_AndCounted()
        {
            var legacy = Legacy(await _service.CreateSnapshotAsync(Guid.NewGuid(), _game));
            Reinstall("game.exe");

            using var locked = new FileStream(Full("game.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
            var (result, entries) = await UpgradeAsync(legacy);

            Assert.Multiple(() =>
            {
                Assert.That(result.Unreadable, Is.EqualTo(1));
                Assert.That(entries.ContainsKey("game.exe"), Is.False);
            });
        }

        [Test]
        public async Task Upgrade_ThroughTheStore_KeepsCreationDate_AndASecondRunHasNothingToDo()
        {
            var directory = Path.Combine(_root, "snapshots");
            var store = new JsonSnapshotStore();
            var legacy = Legacy(await _service.CreateSnapshotAsync(Guid.NewGuid(), _game));
            await store.SaveAsync(directory, legacy);
            var snapshotFile = Path.Combine(directory, legacy.ProfileId.ToString("D") + ".json");
            var createdAt = new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Local);
            File.SetLastWriteTime(snapshotFile, createdAt);

            var (_, entries) = await UpgradeAsync((await store.LoadAsync(directory, legacy.ProfileId))!.Data);
            await store.UpdateEntriesAsync(directory, legacy.ProfileId, entries.Values);

            var loaded = (await store.LoadAsync(directory, legacy.ProfileId))!;
            var (second, secondEntries) = await UpgradeAsync(loaded.Data);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.CreatedAt, Is.EqualTo(createdAt));
                Assert.That(loaded.Data.Directories, Is.EqualTo(legacy.Directories));
                Assert.That(second.AlreadyComplete, Is.EqualTo(legacy.Files.Count));
                Assert.That(secondEntries, Is.Empty);
            });
        }
    }
}
