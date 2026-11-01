using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Service.Services.Settings;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class PortableSettingsTests
    {
        private string _root = string.Empty;

        [SetUp]
        public void SetUp() => _root = TestDirectoryFactory.CreateTemporaryDirectory();

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public async Task Settings_PathsInsideAppFolder_FollowTheFolderWhenItMoves()
        {
            var oldApp = Path.Combine(_root, "OldLocation");
            var newApp = Path.Combine(_root, "NewLocation");
            var outside = Path.Combine(_root, "GoogleDrive", "Backups");
            var store = new JsonSettingsStore();

            var settings = AppSettings.Default(outside) with { SafetyBackupPath = Path.Combine(oldApp, "SafetyBackups") };
            await store.SaveAsync(Path.Combine(oldApp, "settings.json"), settings);

            var json = await File.ReadAllTextAsync(Path.Combine(oldApp, "settings.json"));
            Directory.CreateDirectory(newApp);
            File.Copy(Path.Combine(oldApp, "settings.json"), Path.Combine(newApp, "settings.json"));

            var moved = await store.LoadAsync(Path.Combine(newApp, "settings.json"));

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("\"SafetyBackupPath\": \"SafetyBackups\""), "paths inside the app folder are stored relative");
                Assert.That(moved.SafetyBackupPath, Is.EqualTo(Path.Combine(newApp, "SafetyBackups")));
                Assert.That(moved.GoogleDriveBackupPath, Is.EqualTo(outside), "paths outside the app folder stay absolute");
            });
        }

        [TestCase(@"C:\App\SafetyBackups", @"C:\App", "SafetyBackups")]
        [TestCase(@"C:\App\Backups\Games", @"C:\App", @"Backups\Games")]
        [TestCase(@"D:\Drive\Backups", @"C:\App", @"D:\Drive\Backups")]
        [TestCase(@"C:\Application\X", @"C:\App", @"C:\Application\X")]
        public void ToPortablePath_OnlyRelativizesPathsInsideTheBase(string path, string baseDirectory, string expected)
        {
            Assert.That(JsonSettingsStore.ToPortablePath(path, baseDirectory), Is.EqualTo(expected));
        }
    }
}