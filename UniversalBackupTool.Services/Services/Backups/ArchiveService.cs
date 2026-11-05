using System.IO.Compression;
using System.Security.Cryptography;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Logging;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Settings;

using static System.FormattableString;

namespace UniversalBackupTool.Service.Services.Backups
{
    public sealed class ArchiveService : IArchiveService
    {
        private const int BufferSize = 1024 * 1024;

        private enum FileOutcome
        {
            Written,

            /// <summary>
            /// An empty folder entry: part of the archive, but not a file.
            /// </summary>
            Folder,

            Missing,

            /// <summary>
            /// The file could not be opened, so the archive has no entry for it yet.
            /// </summary>
            Locked,

            /// <summary>
            /// Reading stopped halfway, so the archive holds a partial entry that only Continue Backup can replace.
            /// </summary>
            Failed
        }

        private sealed record FileWriteOutcome(FileOutcome Outcome, string? Sha256);

        private readonly ILogFileWriter? _log;

        public ArchiveService()
            : this(null)
        {
        }

        /// <param name="log">Receives an "Archiving" and an "Archived" line (with duration, size, speed and compression
        /// level) for every file, so slow files can be found in the log and their compression rule adjusted.</param>
        public ArchiveService(ILogFileWriter? log)
        {
            _log = log;
        }

        public Task<ArchiveWriteResult> CreateArchiveAsync(
            string archivePath,
            IReadOnlyList<ArchiveFileItem> files,
            AppSettings settings,
            IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return WriteAsync(archivePath, files, settings, ZipArchiveMode.Create, progress, cancellationToken);
        }

        public Task<ArchiveWriteResult> AppendToArchiveAsync(
            string archivePath,
            IReadOnlyList<ArchiveFileItem> files,
            AppSettings settings,
            IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return WriteAsync(archivePath, files, settings, ZipArchiveMode.Update, progress, cancellationToken);
        }

        private async Task<ArchiveWriteResult> WriteAsync(
            string archivePath,
            IReadOnlyList<ArchiveFileItem> files,
            AppSettings settings,
            ZipArchiveMode mode,
            IProgress<ArchiveProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
                throw new ArgumentException("Archive path cannot be empty.", nameof(archivePath));

            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(settings);

            var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath));

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var failed = new List<ArchiveFileItem>();
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var written = 0;

            var fileMode = mode == ZipArchiveMode.Create ? FileMode.Create : FileMode.OpenOrCreate;

            await using var archiveStream = new FileStream(archivePath, fileMode, FileAccess.ReadWrite, FileShare.None, BufferSize, useAsync: true);
            using var zipArchive = new ZipArchive(archiveStream, mode, leaveOpen: true);

            // A file locked now may be free once the rest is written. Trying again here costs nothing, while Continue
            // Backup has to reopen the finished archive for update. Continue Backup itself does not need a second try.
            var retryLocked = mode == ZipArchiveMode.Create;
            var locked = new List<ArchiveFileItem>();

            foreach (var item in files)
                await AddAsync(item, retryLocked);

            foreach (var item in locked)
                await AddAsync(item, false);

            return new ArchiveWriteResult(written, failed) { FileHashes = hashes };

            async Task AddAsync(ArchiveFileItem item, bool retryIfLocked)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outcome = await TryAddFileAsync(zipArchive, item, settings, mode == ZipArchiveMode.Update, retryIfLocked, progress, cancellationToken);

                if (outcome.Sha256 is not null)
                    hashes[item.SourcePath] = outcome.Sha256;

                switch (outcome.Outcome)
                {
                    case FileOutcome.Written:
                        written++;
                        break;

                    case FileOutcome.Locked when retryIfLocked:
                        locked.Add(item);
                        break;

                    case FileOutcome.Locked:
                    case FileOutcome.Failed:
                        failed.Add(item);
                        break;
                }
            }
        }

        private async Task<FileWriteOutcome> TryAddFileAsync(
            ZipArchive zipArchive,
            ArchiveFileItem item,
            AppSettings settings,
            bool replaceExisting,
            bool retryIfLocked,
            IProgress<ArchiveProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (item.IsEmptyFolder)
            {
                if (replaceExisting)
                    zipArchive.GetEntry(item.EntryName)?.Delete();

                zipArchive.CreateEntry(item.EntryName);
                progress?.Report(new ArchiveProgress(item.SourcePath, 0, true));

                return new FileWriteOutcome(FileOutcome.Folder, null);
            }

            FileStream input;

            try
            {
                // ReadWrite sharing lets us read files that another process (the game, an antivirus) keeps open for writing.
                input = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                _log?.AppendDetail($"Skipped (file no longer exists): {item.SourcePath}");
                progress?.Report(new ArchiveProgress(item.SourcePath, 0, true));
                return new FileWriteOutcome(FileOutcome.Missing, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (retryIfLocked)
                {
                    _log?.AppendDetail($"Locked (in use or access denied, trying again at the end of this archive): {item.SourcePath} - {exception.Message}");
                }
                else
                {
                    _log?.AppendDetail($"Skipped (in use or access denied, will be retried by Continue Backup): {item.SourcePath} - {exception.Message}");
                    progress?.Report(new ArchiveProgress(item.SourcePath, 0, true));
                }

                return new FileWriteOutcome(FileOutcome.Locked, null);
            }

            var readFailed = false;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var length = input.Length;
            var level = GetCompressionLevel(item.SourcePath, length, settings);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _log?.AppendDetail(Invariant($"Archiving: {item.SourcePath} ({FormatSize(length)}, level {level})"));

            await using (input)
            {
                if (replaceExisting)
                    zipArchive.GetEntry(item.EntryName)?.Delete();

                var entry = zipArchive.CreateEntry(item.EntryName, level);
                entry.LastWriteTime = ClampToZipRange(File.GetLastWriteTime(item.SourcePath));

                await using var output = entry.Open();

                var buffer = new byte[BufferSize];

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int read;

                    try
                    {
                        read = await input.ReadAsync(buffer, cancellationToken);
                    }
                    catch (IOException)
                    {
                        readFailed = true;
                        break;
                    }

                    if (read == 0)
                        break;

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                    progress?.Report(new ArchiveProgress(item.SourcePath, read, false));
                }
            }

            // The entry stream is closed above, so the elapsed time includes finishing the compression.
            stopwatch.Stop();

            if (readFailed)
            {
                _log?.AppendDetail(Invariant($"Failed while reading (in use, will be retried by Continue Backup): {item.SourcePath} after {stopwatch.Elapsed.TotalSeconds:0.000} s"));
            }
            else
            {
                var seconds = stopwatch.Elapsed.TotalSeconds;
                var speed = seconds > 0 ? $", {FormatSize((long)(length / seconds))}/s" : string.Empty;
                _log?.AppendDetail(Invariant($"Archived:  {item.SourcePath} in {seconds:0.000} s ({FormatSize(length)}{speed}, level {level})"));
            }

            progress?.Report(new ArchiveProgress(item.SourcePath, 0, true));

            return readFailed
                ? new FileWriteOutcome(FileOutcome.Failed, null)
                : new FileWriteOutcome(FileOutcome.Written, Convert.ToHexString(hash.GetHashAndReset()));
        }

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            var unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? Invariant($"{bytes} B") : Invariant($"{value:0.0} {units[unit]}");
        }

        /// <summary>
        /// Types that are compressed already are stored; otherwise the smallest size rule the file fits decides.
        /// </summary>
        private static CompressionLevel GetCompressionLevel(string path, long fileLength, AppSettings settings)
        {
            if (settings.IsNoCompressionFile(path))
                return CompressionLevel.NoCompression;

            var matchedRule = settings.CompressionRules
                .OrderBy(rule => rule.MaxSizeBytes)
                .FirstOrDefault(rule => fileLength <= rule.MaxSizeBytes);

            return matchedRule?.CompressionLevel ?? CompressionLevel.NoCompression;
        }

        private static DateTimeOffset ClampToZipRange(DateTime value)
        {
            var min = new DateTime(1980, 1, 1);
            var max = new DateTime(2107, 12, 31);

            return value < min ? min : value > max ? max : value;
        }
    }
}