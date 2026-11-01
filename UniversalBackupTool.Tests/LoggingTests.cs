using UniversalBackupTool.Model.Models.Operations;
using UniversalBackupTool.Service.Services.Logging;
using UniversalBackupTool.Service.Services.Operations;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class LoggingTests
    {
        private string _root = string.Empty;
        private FileLogWriter _writer = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _writer = new FileLogWriter(new FakeStoragePathsProvider(_root));
        }

        [TearDown]
        public void TearDown()
        {
            _writer.Dispose();

            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void Append_WritesToDailyLogFile()
        {
            var timestamp = new DateTime(2026, 10, 31, 20, 15, 0);

            _writer.Append(new OutputEntry(timestamp, OutputSeverity.Warning, "Something to note"));

            var text = File.ReadAllText(_writer.GetLogPath(timestamp));
            Assert.That(text, Does.Contain("[20:15:00] WARNING Something to note"));
        }

        [Test]
        public void AppendException_WritesFullDetails()
        {
            try
            {
                throw new InvalidOperationException("outer", new IOException("inner cause"));
            }
            catch (Exception exception)
            {
                _writer.AppendException("Unhandled UI error", exception);
            }

            var text = File.ReadAllText(_writer.GetLogPath(DateTime.Now));

            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("EXCEPTION Unhandled UI error"));
                Assert.That(text, Does.Contain("System.InvalidOperationException: outer"));
                Assert.That(text, Does.Contain("inner cause"));
            });
        }

        [Test]
        public void FirstWrite_DeletesLogsOlderThanRetention()
        {
            Directory.CreateDirectory(_writer.LogsDirectory);
            var old = Path.Combine(_writer.LogsDirectory, "log_2000-01-01.txt");
            var recent = Path.Combine(_writer.LogsDirectory, "log_recent.txt");
            File.WriteAllText(old, "old");
            File.WriteAllText(recent, "recent");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-(FileLogWriter.RetentionDays + 5)));

            _writer.Append(new OutputEntry(DateTime.Now, OutputSeverity.Info, "hello"));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(old), Is.False);
                Assert.That(File.Exists(recent), Is.True);
            });
        }

        [Test]
        public void AppendDetail_IsBatched_AndHasFullDateAndMilliseconds()
        {
            _writer.AppendDetail("Archiving: C:\\mods\\a.esp (1.0 KB, level Optimal)");
            _writer.Flush();

            var text = File.ReadAllText(_writer.GetLogPath(DateTime.Now));

            Assert.That(text, Does.Match(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] Archiving: C:\\mods\\a\.esp"));
        }

        [Test]
        public async Task ArchiveService_LogsStartAndFinishForEachFile()
        {
            var source = Path.Combine(_root, "source");
            Directory.CreateDirectory(source);
            var small = Path.Combine(source, "small.txt");
            var locked = Path.Combine(source, "locked.bin");
            File.WriteAllText(small, new string('x', 4096));
            File.WriteAllText(locked, "locked");

            var archive = new UniversalBackupTool.Service.Services.Backups.ArchiveService(_writer);

            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await archive.CreateArchiveAsync(
                    Path.Combine(_root, "out.zip"),
                    [new(small, "small.txt"), new(locked, "locked.bin")],
                    UniversalBackupTool.Model.Models.Settings.AppSettings.Default(_root));
            }

            _writer.Flush();
            var lines = File.ReadAllLines(_writer.GetLogPath(DateTime.Now));

            Assert.Multiple(() =>
            {
                Assert.That(lines, Has.Some.Contains($"Archiving: {small} (4.0 KB, level Optimal)"));
                Assert.That(lines, Has.Some.Match($@"Archived:  {System.Text.RegularExpressions.Regex.Escape(small)} in \d+\.\d{{3}} s \(4\.0 KB.*level Optimal\)"));
                Assert.That(lines, Has.Some.Contains($"Skipped (in use or access denied, will be retried by Continue Backup): {locked}"));
            });
        }

        [Test]
        public void OutputService_MirrorsEntriesToLog_AndClearKeepsTheLog()
        {
            var output = new OperationOutputService(_writer);

            output.Write("Backup finished", OutputSeverity.Success);
            output.Clear();

            Assert.Multiple(() =>
            {
                Assert.That(output.Entries, Is.Empty);
                Assert.That(File.ReadAllText(_writer.GetLogPath(DateTime.Now)), Does.Contain("SUCCESS Backup finished"));
            });
        }
    }
}