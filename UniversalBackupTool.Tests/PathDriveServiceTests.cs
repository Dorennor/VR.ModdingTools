using UniversalBackupTool.Interface.Interfaces.Paths;
using UniversalBackupTool.Service.Services.Paths;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class PathDriveServiceTests
    {
        private sealed class FakeSystemPathsProvider : ISystemPathsProvider
        {
            public List<string> Drives { get; } = ["C:", "D:", "E:"];

            public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

            public int ExistsCallCount { get; private set; }

            public IReadOnlyList<string> GetAvailableDrives() => Drives;

            public string GetDefaultVortexAppDataPath() => @"C:\Users\Test\AppData\Roaming\Vortex";

            public bool PathExists(string path)
            {
                ExistsCallCount++;
                return ExistingPaths.Contains(path);
            }
        }

        private FakeSystemPathsProvider _system = null!;
        private PathDriveService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _system = new FakeSystemPathsProvider();
            _service = new PathDriveService(_system);
        }

        [Test]
        public void Constructor_NullProvider_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new PathDriveService(null!));
        }

        [TestCase(@"D:\Mods", "D:")]
        [TestCase(@"d:\Mods", "D:")]
        [TestCase("D:", "D:")]
        [TestCase(@"D:\", "D:")]
        [TestCase("D:/Mods", "D:")]
        public void GetDrive_RootedPath_ReturnsUpperCaseDrive(string path, string expected)
        {
            Assert.That(_service.GetDrive(path), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("D")]
        [TestCase(@"Documents\Mods")]
        [TestCase(@"\\server\share")]
        [TestCase(@"DD:\x")]
        [TestCase("D:Mods")]
        [TestCase(@"1:\x")]
        public void GetDrive_NoDrive_ReturnsNull(string? path)
        {
            Assert.That(_service.GetDrive(path), Is.Null);
        }

        [TestCase("E")]
        [TestCase("e:")]
        [TestCase(@"E:\")]
        [TestCase(" E ")]
        public void ReplaceDrive_AcceptsDifferentDriveFormats(string newDrive)
        {
            Assert.That(_service.ReplaceDrive(@"D:\Mods\skyrim", newDrive), Is.EqualTo(@"E:\Mods\skyrim"));
        }

        [Test]
        public void ReplaceDrive_KeepsRestOfPathUntouched()
        {
            Assert.That(_service.ReplaceDrive(@"d:\Some D:\Folder With Spaces\", "C"), Is.EqualTo(@"C:\Some D:\Folder With Spaces\"));
        }

        [Test]
        public void ReplaceDrive_DriveRootOnly_Works()
        {
            Assert.That(_service.ReplaceDrive("D:", "E"), Is.EqualTo("E:"));
            Assert.That(_service.ReplaceDrive(@"D:\", "E"), Is.EqualTo(@"E:\"));
        }

        [Test]
        public void ReplaceDrive_SameDrive_ReturnsEquivalentPath()
        {
            Assert.That(_service.ReplaceDrive(@"D:\Mods", "D"), Is.EqualTo(@"D:\Mods"));
        }

        [TestCase(@"Documents\Mods")]
        [TestCase(@"\\server\share")]
        [TestCase("")]
        public void ReplaceDrive_PathWithoutDrive_Throws(string path)
        {
            Assert.Throws<ArgumentException>(() => _service.ReplaceDrive(path, "E"));
        }

        [TestCase("")]
        [TestCase("  ")]
        [TestCase("EF")]
        [TestCase("1")]
        [TestCase(@"E:\Mods")]
        public void ReplaceDrive_InvalidNewDrive_Throws(string newDrive)
        {
            Assert.Throws<ArgumentException>(() => _service.ReplaceDrive(@"D:\Mods", newDrive));
        }

        [Test]
        public void FindOnOtherDrives_ReturnsDrivesWhereThePathExists()
        {
            _system.ExistingPaths.Add(@"E:\Vortex Mods\skyrimse");

            var found = _service.FindOnOtherDrives(@"D:\Vortex Mods\skyrimse");

            Assert.That(found, Is.EqualTo(new[] { @"E:\Vortex Mods\skyrimse" }));
        }

        [Test]
        public void FindOnOtherDrives_ReturnsAllMatches_InDriveOrder()
        {
            _system.ExistingPaths.Add(@"C:\Mods");
            _system.ExistingPaths.Add(@"E:\Mods");

            var found = _service.FindOnOtherDrives(@"D:\Mods");

            Assert.That(found, Is.EqualTo(new[] { @"C:\Mods", @"E:\Mods" }));
        }

        [Test]
        public void FindOnOtherDrives_NotFoundAnywhere_ReturnsEmpty()
        {
            Assert.That(_service.FindOnOtherDrives(@"D:\Mods"), Is.Empty);
        }

        [Test]
        public void FindOnOtherDrives_DoesNotCheckTheCurrentDrive()
        {
            _system.ExistingPaths.Add(@"D:\Mods");

            var found = _service.FindOnOtherDrives(@"D:\Mods");

            Assert.That(found, Is.Empty);
            Assert.That(_system.ExistsCallCount, Is.EqualTo(2));
        }

        [Test]
        public void FindOnOtherDrives_PathWithoutDrive_ReturnsEmptyWithoutChecking()
        {
            Assert.That(_service.FindOnOtherDrives(@"Documents\Mods"), Is.Empty);
            Assert.That(_system.ExistsCallCount, Is.Zero);
        }

        [Test]
        public void FindOnOtherDrives_IsCaseInsensitiveForCurrentDrive()
        {
            _system.ExistingPaths.Add(@"E:\Mods");

            Assert.That(_service.FindOnOtherDrives(@"d:\Mods"), Is.EqualTo(new[] { @"E:\Mods" }));
        }

        [Test]
        public void FindOnOtherDrives_NoDrivesAvailable_ReturnsEmpty()
        {
            _system.Drives.Clear();

            Assert.That(_service.FindOnOtherDrives(@"D:\Mods"), Is.Empty);
        }

        [Test]
        public void FindOnOtherDrives_DuplicateDrivesInProvider_AreReportedOnce()
        {
            _system.Drives.Add("e:");
            _system.ExistingPaths.Add(@"E:\Mods");

            Assert.That(_service.FindOnOtherDrives(@"D:\Mods"), Is.EqualTo(new[] { @"E:\Mods" }));
        }

        [Test]
        public void FindOnOtherDrives_DriveRoot_ChecksRootOfOtherDrives()
        {
            _system.ExistingPaths.Add(@"E:\");

            Assert.That(_service.FindOnOtherDrives("D:"), Is.EqualTo(new[] { @"E:\" }));
        }
    }
}