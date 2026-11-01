using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Service.Services.Profiles;
using UniversalBackupTool.Service.Services.Settings;

namespace UniversalBackupTool.Tests
{
    /// <summary>
    /// The Vortex AppData folder moved from each profile into the settings (defaults version 3). Existing installations
    /// must be upgraded on load without losing anything the user configured.
    /// </summary>
    [TestFixture]
    public class VortexAppDataSettingsTests
    {
        private string _root = string.Empty;
        private string _settingsPath = string.Empty;
        private string _profilesPath = string.Empty;

        // settings.json and profiles.json exactly as the production installation wrote them before this change.
        private const string VersionTwoSettings = """
            {
              "GoogleDriveBackupPath": "G:\\\u041C\u0456\u0439 \u0434\u0438\u0441\u043A\\Game files\\Games backups",
              "SafetyBackupRetentionDays": 14,
              "CompressionRules": [
                { "MaxSizeBytes": 5242880, "CompressionLevel": 3 },
                { "MaxSizeBytes": 10485760, "CompressionLevel": 0 },
                { "MaxSizeBytes": 52428800, "CompressionLevel": 1 },
                { "MaxSizeBytes": 9223372036854775807, "CompressionLevel": 2 }
              ],
              "GlobalIgnorePatterns": [ "vortex.deployment*.json", "__folder_managed_by_vortex", "*.log" ],
              "Theme": 0,
              "SafetyBackupPath": "G:\\\u041C\u0456\u0439 \u0434\u0438\u0441\u043A\\Game files\\Games backups\\SafetyBackups",
              "MaxPreservedBackupsPerGame": 3,
              "DefaultsVersion": 2
            }
            """;

        private const string VersionTwoProfiles = """
            [
              {
                "Id": "05983caa-4443-423c-9d43-04079c035228",
                "GameName": "Anno 1404 - History Edition",
                "UsesVortex": false,
                "VortexStagingPath": null,
                "GameRootPath": "E:\\Games\\SteamLibrary\\steamapps\\common\\Anno 1404 - History Edition",
                "VortexAppDataPath": null,
                "CustomFolders": [],
                "UseIgnoreList": true,
                "CreatedAtUtc": "2026-11-01T03:41:59.263831+02:00"
              },
              {
                "Id": "d3e61fbe-494e-4d15-9462-92d6985bb0a2",
                "GameName": "Valheim",
                "UsesVortex": true,
                "VortexStagingPath": "E:\\Vortex Mods\\valheim",
                "GameRootPath": "E:\\Games\\SteamLibrary\\steamapps\\common\\Valheim",
                "VortexAppDataPath": "D:\\Custom\\Vortex",
                "CustomFolders": [],
                "UseIgnoreList": true,
                "CreatedAtUtc": "2026-11-01T03:55:40.2357754+02:00"
              }
            ]
            """;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _settingsPath = Path.Combine(_root, "settings.json");
            _profilesPath = Path.Combine(_root, "profiles.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public async Task ProductionSettings_TakeTheAppDataPathOfTheVortexProfile_AndKeepEverythingElse()
        {
            await File.WriteAllTextAsync(_settingsPath, VersionTwoSettings);
            await File.WriteAllTextAsync(_profilesPath, VersionTwoProfiles);

            var migrated = await new JsonSettingsStore().LoadAsync(_settingsPath);
            var reloaded = await new JsonSettingsStore().LoadAsync(_settingsPath);

            Assert.Multiple(() =>
            {
                Assert.That(migrated.VortexAppDataPath, Is.EqualTo(@"D:\Custom\Vortex"));
                Assert.That(migrated.DefaultsVersion, Is.EqualTo(AppSettings.CurrentDefaultsVersion));
                Assert.That(migrated.GoogleDriveBackupPath, Is.EqualTo(@"G:\Мій диск\Game files\Games backups"));
                Assert.That(migrated.SafetyBackupPath, Is.EqualTo(@"G:\Мій диск\Game files\Games backups\SafetyBackups"));
                Assert.That(migrated.GlobalIgnorePatterns, Is.EqualTo(new[] { "vortex.deployment*.json", "__folder_managed_by_vortex", "*.log" }));
                Assert.That(migrated.CompressionRules, Has.Count.EqualTo(4));
                Assert.That(migrated.MaxPreservedBackupsPerGame, Is.EqualTo(3));
                Assert.That(reloaded.VortexAppDataPath, Is.EqualTo(@"D:\Custom\Vortex"), "the upgrade is saved");
                Assert.That(File.ReadAllText(_settingsPath), Does.Contain($"\"DefaultsVersion\": {AppSettings.CurrentDefaultsVersion}"));
                Assert.That(migrated.NoCompressionExtensions, Is.EqualTo(AppSettings.DefaultNoCompressionExtensions), "compression speed defaults are added too");
                Assert.That(migrated.ParallelArchives, Is.EqualTo(Environment.ProcessorCount));
                Assert.That(File.ReadAllText(_profilesPath), Is.EqualTo(VersionTwoProfiles), "profiles.json is not rewritten by the upgrade");
            });
        }

        [Test]
        public async Task OldSettings_WithoutVortexProfilePath_GetTheUsualFolder()
        {
            await File.WriteAllTextAsync(_settingsPath, VersionTwoSettings);
            await File.WriteAllTextAsync(_profilesPath, """
                [
                  { "GameName": "Anno", "UsesVortex": false, "VortexAppDataPath": "D:\\Ignored\\Vortex" },
                  { "GameName": "Skyrim", "VortexAppDataPath": "" }
                ]
                """);

            var migrated = await new JsonSettingsStore().LoadAsync(_settingsPath);

            Assert.That(migrated.VortexAppDataPath, Is.EqualTo(AppSettings.DefaultVortexAppDataPath));
        }

        [Test]
        public async Task OldSettings_WithoutProfilesFile_GetTheUsualFolder()
        {
            await File.WriteAllTextAsync(_settingsPath, VersionTwoSettings);

            var migrated = await new JsonSettingsStore().LoadAsync(_settingsPath);

            Assert.That(migrated.VortexAppDataPath, Is.EqualTo(AppSettings.DefaultVortexAppDataPath));
        }

        [Test]
        public async Task ProfilesFromBeforeUsesVortex_CountAsVortexProfiles()
        {
            await File.WriteAllTextAsync(_settingsPath, """{ "GoogleDriveBackupPath": "D:\\Backups" }""");
            await File.WriteAllTextAsync(_profilesPath, """[ { "GameName": "Skyrim", "VortexAppDataPath": "E:\\Vortex" } ]""");

            var migrated = await new JsonSettingsStore().LoadAsync(_settingsPath);

            Assert.Multiple(() =>
            {
                Assert.That(migrated.VortexAppDataPath, Is.EqualTo(@"E:\Vortex"));
                Assert.That(migrated.GlobalIgnorePatterns, Is.EqualTo(AppSettings.DefaultIgnorePatterns), "older upgrades still run");
            });
        }

        [Test]
        public async Task UnreadableProfilesFile_DoesNotBreakLoading()
        {
            await File.WriteAllTextAsync(_settingsPath, VersionTwoSettings);
            await File.WriteAllTextAsync(_profilesPath, "{ not json");

            var migrated = await new JsonSettingsStore().LoadAsync(_settingsPath);

            Assert.That(migrated.VortexAppDataPath, Is.EqualTo(AppSettings.DefaultVortexAppDataPath));
        }

        [Test]
        public async Task CurrentSettings_KeepTheirAppDataPath()
        {
            var store = new JsonSettingsStore();
            await store.SaveAsync(_settingsPath, AppSettings.Default(@"D:\Backups") with { VortexAppDataPath = @"F:\Portable Vortex\data" });
            await File.WriteAllTextAsync(_profilesPath, VersionTwoProfiles);

            var loaded = await store.LoadAsync(_settingsPath);

            Assert.That(loaded.VortexAppDataPath, Is.EqualTo(@"F:\Portable Vortex\data"));
        }

        [Test]
        public async Task NewInstallation_GetsTheUsualFolder()
        {
            var created = await new JsonSettingsStore().LoadAsync(_settingsPath);

            Assert.That(created.VortexAppDataPath, Is.EqualTo(AppSettings.DefaultVortexAppDataPath));
        }

        [Test]
        public async Task ProfileStore_StillLoadsTheProductionProfiles()
        {
            await File.WriteAllTextAsync(_profilesPath, VersionTwoProfiles);

            var profiles = await new JsonProfileStore().LoadAsync(_profilesPath);

            Assert.Multiple(() =>
            {
                Assert.That(profiles.Select(profile => profile.GameName), Is.EqualTo(new[] { "Anno 1404 - History Edition", "Valheim" }));
                Assert.That(profiles[1].VortexStagingPath, Is.EqualTo(@"E:\Vortex Mods\valheim"));
                Assert.That(profiles[0].UsesVortex, Is.False);
            });
        }

        [TestCase("#Vortex AppData#", true)]
        [TestCase("#vortex appdata#", true)]
        [TestCase("  #Vortex AppData#. ", true)]
        [TestCase("Vortex", false)]
        [TestCase("Vortex AppData", false)]
        [TestCase("", false)]
        public void SharedFolderName_IsReservedForProfiles(string gameName, bool reserved)
        {
            Assert.That(VortexAppDataBackup.IsReservedName(gameName), Is.EqualTo(reserved));
        }
    }
}