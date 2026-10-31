using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Operations;
using UniversalBackupTool.Service.Services.Operations;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class OperationServicesTests
    {
        private OperationOutputService _output = null!;
        private OperationCoordinator _coordinator = null!;

        [SetUp]
        public void SetUp()
        {
            // No SynchronizationContext in tests, so both services work synchronously.
            _output = new OperationOutputService();
            _coordinator = new OperationCoordinator(_output);
        }

        [Test]
        public void Output_Write_AddsEntryWithSeverity()
        {
            _output.Write("  Backup finished.  ", OutputSeverity.Success);

            Assert.That(_output.Entries, Has.Count.EqualTo(1));
            Assert.That(_output.Entries[0].Message, Is.EqualTo("Backup finished."));
            Assert.That(_output.Entries[0].Severity, Is.EqualTo(OutputSeverity.Success));
        }

        [Test]
        public void Output_Write_IgnoresEmptyMessages()
        {
            _output.Write("   ");

            Assert.That(_output.Entries, Is.Empty);
        }

        [Test]
        public void Output_KeepsOnlyNewestEntries()
        {
            for (var index = 0; index < OperationOutputService.MaxEntries + 10; index++)
                _output.Write($"line {index}");

            Assert.That(_output.Entries, Has.Count.EqualTo(OperationOutputService.MaxEntries));
            Assert.That(_output.Entries[0].Message, Is.EqualTo("line 10"));
        }

        [Test]
        public void Output_Clear_RemovesEntries()
        {
            _output.Write("a");
            _output.Clear();

            Assert.That(_output.Entries, Is.Empty);
        }

        [Test]
        public void TryBegin_WhileRunning_ReturnsNull()
        {
            using var first = _coordinator.TryBegin("Backup");

            Assert.That(first, Is.Not.Null);
            Assert.That(_coordinator.TryBegin("Restore"), Is.Null);
            Assert.That(_coordinator.CurrentOperationName, Is.EqualTo("Backup"));
        }

        [Test]
        public void DisposingScope_EndsOperation()
        {
            var scope = _coordinator.TryBegin("Backup")!;
            scope.Dispose();

            Assert.That(_coordinator.IsOperationRunning, Is.False);
            Assert.That(_coordinator.CurrentOperationName, Is.Null);

            using var next = _coordinator.TryBegin("Restore");
            Assert.That(next, Is.Not.Null);
        }

        [Test]
        public void DisposingScopeTwice_DoesNotEndNextOperation()
        {
            var scope = _coordinator.TryBegin("Backup")!;
            scope.Dispose();

            using var next = _coordinator.TryBegin("Restore");
            scope.Dispose();

            Assert.That(_coordinator.CurrentOperationName, Is.EqualTo("Restore"));
        }

        [Test]
        public void CancelCurrent_CancelsScopeTokenAndWritesWarning()
        {
            using var scope = _coordinator.TryBegin("Backup")!;

            _coordinator.CancelCurrent();

            Assert.That(scope.CancellationToken.IsCancellationRequested, Is.True);
            Assert.That(_coordinator.IsCancellationRequested, Is.True);
            Assert.That(_output.Entries.Any(entry => entry.Severity == OutputSeverity.Warning), Is.True);
        }

        [Test]
        public void CancelCurrent_WithoutOperation_DoesNothing()
        {
            Assert.DoesNotThrow(_coordinator.CancelCurrent);
            Assert.That(_output.Entries, Is.Empty);
        }

        [Test]
        public void Report_UpdatesProgressAndLogsEachPhaseOnce()
        {
            var changes = 0;
            _coordinator.StateChanged += (_, _) => changes++;

            using var scope = _coordinator.TryBegin("Backup")!;

            scope.Report(new BackupProgress("scanning", null, 0, 0, 0, 0));
            scope.Report(new BackupProgress("archiving", "a.esp", 10, 100, 1, 10));
            scope.Report(new BackupProgress("archiving", "b.esp", 50, 100, 5, 10));

            Assert.That(_coordinator.CurrentProgress?.BytesProcessed, Is.EqualTo(50));
            Assert.That(_output.Entries.Select(entry => entry.Message), Is.EqualTo(new[] { "Backup: scanning", "Backup: archiving" }));
            Assert.That(changes, Is.GreaterThanOrEqualTo(4));
        }

        [Test]
        public void Report_AfterScopeEnded_IsIgnored()
        {
            var scope = _coordinator.TryBegin("Backup")!;
            scope.Dispose();

            scope.Report(new BackupProgress("late", null, 0, 0, 0, 0));

            Assert.That(_coordinator.CurrentProgress, Is.Null);
            Assert.That(_output.Entries, Is.Empty);
        }
    }
}
