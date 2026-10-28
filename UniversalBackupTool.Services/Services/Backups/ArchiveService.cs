using System.IO.Compression;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Service.Services.Backups
{
    public sealed class ArchiveService : IArchiveService
    {
        private const int BufferSize = 1024 * 1024;

        private enum FileOutcome
        {
            Written,
            Missing,
            Failed
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

        private static async Task<ArchiveWriteResult> WriteAsync(
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
            var written = 0;

            var fileMode = mode == ZipArchiveMode.Create ? FileMode.Create : FileMode.OpenOrCreate;

            await using var archiveStream = new FileStream(archivePath, fileMode, FileAccess.ReadWrite, FileShare.None, BufferSize, useAsync: true);
            using var zipArchive = new ZipArchive(archiveStream, mode, leaveOpen: true);

            foreach (var item in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outcome = await TryAddFileAsync(zipArchive, item, settings, mode == ZipArchiveMode.Update, progress, cancellationToken);

                switch (outcome)
                {
                    case FileOutcome.Written:
                        written++;
                        break;

                    case FileOutcome.Failed:
                        failed.Add(item);
                        break;
                }
            }

            return new ArchiveWriteResult(written, failed);
        }

        private static async Task<FileOutcome> TryAddFileAsync(
            ZipArchive zipArchive,
            ArchiveFileItem item,
            AppSettings settings,
            bool replaceExisting,
            IProgress<ArchiveProgress>? progress,
            CancellationToken cancellationToken)
        {
            FileStream input;

            try
            {
                // ReadWrite sharing lets us read files that another process (the game, an antivirus) keeps open for writing.
                input = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                progress?.Report(new ArchiveProgress(item.SourcePath, 0, true));
                return FileOutcome.Missing;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                progress?.Report(new ArchiveProgress(item.SourcePath, 0, true));
                return FileOutcome.Failed;
            }

            var readFailed = false;

            await using (input)
            {
                if (replaceExisting)
                    zipArchive.GetEntry(item.EntryName)?.Delete();

                var entry = zipArchive.CreateEntry(item.EntryName, GetCompressionLevel(input.Length, settings));
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

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                    progress?.Report(new ArchiveProgress(item.SourcePath, read, false));
                }
            }

            progress?.Report(new ArchiveProgress(item.SourcePath, 0, true));

            return readFailed ? FileOutcome.Failed : FileOutcome.Written;
        }

        private static CompressionLevel GetCompressionLevel(long fileLength, AppSettings settings)
        {
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