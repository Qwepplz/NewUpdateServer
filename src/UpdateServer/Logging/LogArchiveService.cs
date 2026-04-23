using System;
using System.Collections.Generic;
using System.IO;
using UpdateServer.Compression;
using UpdateServer.Config;
using UpdateServer.FileSystem;

namespace UpdateServer.Logging
{
    internal static class LogArchiveService
    {
        private static readonly ILogArchiveCompressor Compressor = new ManagedSevenZipLogArchiveCompressor();

        internal static void TryArchivePreviousLogs(string targetDir, string currentLogPath, Action<string> writeLogLine)
        {
            if (string.IsNullOrWhiteSpace(targetDir) || string.IsNullOrWhiteSpace(currentLogPath) || writeLogLine == null)
            {
                return;
            }

            string[] archiveCandidates = GetArchiveCandidates(targetDir, currentLogPath);

            int archivedCount = 0;
            foreach (string logFilePath in archiveCandidates)
            {
                if (TryArchiveSingleLog(targetDir, logFilePath, writeLogLine))
                {
                    archivedCount++;
                }
            }

            if (archivedCount > 0)
            {
                writeLogLine("Archived previous log files: " + archivedCount);
            }
        }

        private static string[] GetArchiveCandidates(string targetDir, string currentLogPath)
        {
            string normalizedCurrentLogPath = SyncPathUtility.GetFullPath(currentLogPath);
            string logDirectoryPath = SyncPathUtility.GetLogDirectoryPath(targetDir);
            if (!Directory.Exists(logDirectoryPath))
            {
                return new string[0];
            }

            string[] logFilePaths = Directory.GetFiles(
                logDirectoryPath,
                SyncConfiguration.LogFilePrefix + "*" + SyncConfiguration.LogFileExtension);

            if (logFilePaths.Length == 0)
            {
                return logFilePaths;
            }

            Array.Sort(logFilePaths, StringComparer.OrdinalIgnoreCase);

            List<string> archiveCandidates = new List<string>();
            foreach (string logFilePath in logFilePaths)
            {
                string fullLogPath = SyncPathUtility.GetFullPath(logFilePath);
                if (string.Equals(fullLogPath, normalizedCurrentLogPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                archiveCandidates.Add(fullLogPath);
            }

            return archiveCandidates.ToArray();
        }

        private static bool TryArchiveSingleLog(string targetDir, string logPath, Action<string> writeLogLine)
        {
            string archivePath = Path.ChangeExtension(logPath, SyncConfiguration.LogArchiveExtension);
            string tempArchivePath = archivePath + SyncConfiguration.LogArchiveTempExtension;

            SafePathService.AssertSafeManagedPath(targetDir, logPath);
            SafePathService.AssertSafeManagedPath(targetDir, archivePath);
            SafePathService.AssertSafeManagedPath(targetDir, tempArchivePath);

            TryDeleteFile(tempArchivePath);

            try
            {
                Compressor.CompressToArchive(logPath, tempArchivePath);
                TryDeleteFile(archivePath);
                File.Move(tempArchivePath, archivePath);
                File.Delete(logPath);
                writeLogLine("Archived old log: " + Path.GetFileName(logPath) + " -> " + Path.GetFileName(archivePath));
                return true;
            }
            catch (Exception exception)
            {
                TryDeleteFile(tempArchivePath);
                writeLogLine("Old log compression failed: " + Path.GetFileName(logPath) + " => " + exception.Message);
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
