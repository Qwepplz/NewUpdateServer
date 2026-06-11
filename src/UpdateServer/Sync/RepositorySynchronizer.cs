using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UpdateServer.Config;
using UpdateServer.ConsoleUi;
using UpdateServer.FileSystem;
using UpdateServer.Logging;
using UpdateServer.Remote;
using UpdateServer.Remote.Models;
using UpdateServer.Security;
using UpdateServer.State;

namespace UpdateServer.Sync
{
    internal interface IRepositorySynchronizer
    {
        SyncSummary Synchronize(RepositoryTarget repository, TreeResult treeResult, RepositoryRemoteKind remoteKind, string targetDirectoryPath, string stateRoot, HashSet<string> protectedPaths, string tempRootDirectoryPath, LogSession activeLog);
    }

    internal sealed class RepositorySynchronizer : IRepositorySynchronizer
    {
        private readonly IRemoteRepositoryClient remoteRepositoryClient;
        private readonly ISafePathService safePathService;
        private readonly IAtomicFileWriter atomicFileWriter;
        private readonly ISyncStateStore syncStateStore;
        private readonly IGitBlobHasher gitBlobHasher;

        private sealed class SynchronizationContext
        {
            internal RepositoryTarget Repository { get; set; }
            internal TreeResult TreeResult { get; set; }
            internal RepositoryRemoteKind RemoteKind { get; set; }
            internal string TargetDirectoryPath { get; set; }
            internal string StateRoot { get; set; }
            internal HashSet<string> ProtectedPaths { get; set; }
            internal string TempRootDirectoryPath { get; set; }
            internal LogSession ActiveLog { get; set; }
            internal SyncSummary Summary { get; private set; }
            internal string StateDirectoryPath { get; set; }
            internal string ManifestPath { get; set; }
            internal ImportedState ImportedState { get; set; }
            internal Dictionary<string, CachedFileState> CachedFiles { get; set; }
            internal Dictionary<string, CachedFileState> NewCachedFiles { get; set; }
            internal Dictionary<string, TreeEntry> RemoteFiles { get; set; }
            internal Dictionary<string, TreeEntry> ExcludedFiles { get; set; }
            internal List<string> SkippedConflictFiles { get; set; }
            internal List<string> NewManifest { get; set; }
            internal TextWriter ProgressWriter { get; set; }

            internal SynchronizationContext()
            {
                this.Summary = new SyncSummary();
            }
        }

        public RepositorySynchronizer(
            IRemoteRepositoryClient remoteRepositoryClient,
            ISafePathService safePathService,
            IAtomicFileWriter atomicFileWriter,
            ISyncStateStore syncStateStore,
            IGitBlobHasher gitBlobHasher)
        {
            if (remoteRepositoryClient == null) throw new ArgumentNullException(nameof(remoteRepositoryClient));
            if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));
            if (atomicFileWriter == null) throw new ArgumentNullException(nameof(atomicFileWriter));
            if (syncStateStore == null) throw new ArgumentNullException(nameof(syncStateStore));
            if (gitBlobHasher == null) throw new ArgumentNullException(nameof(gitBlobHasher));

            this.remoteRepositoryClient = remoteRepositoryClient;
            this.safePathService = safePathService;
            this.atomicFileWriter = atomicFileWriter;
            this.syncStateStore = syncStateStore;
            this.gitBlobHasher = gitBlobHasher;
        }

        public SyncSummary Synchronize(RepositoryTarget repository, TreeResult treeResult, RepositoryRemoteKind remoteKind, string targetDirectoryPath, string stateRoot, HashSet<string> protectedPaths, string tempRootDirectoryPath, LogSession activeLog)
        {
            SynchronizationContext context = this.CreateContext(repository, treeResult, remoteKind, targetDirectoryPath, stateRoot, protectedPaths, tempRootDirectoryPath, activeLog);

            this.PrepareWorkingState(context);
            PrintRepositoryAccessReady(context);
            this.ClassifyRemoteEntries(context);
            this.RemoveExcludedRootFilesWhenSafe(context);
            this.LogSkippedConflictFiles(context);
            this.DownloadAndUpdateFiles(context);
            this.RemoveUpstreamDeletedFiles(context);
            this.PersistState(context);

            return context.Summary;
        }

        private SynchronizationContext CreateContext(RepositoryTarget repository, TreeResult treeResult, RepositoryRemoteKind remoteKind, string targetDirectoryPath, string stateRoot, HashSet<string> protectedPaths, string tempRootDirectoryPath, LogSession activeLog)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (treeResult == null) throw new ArgumentNullException(nameof(treeResult));
            if (treeResult.Tree == null) throw new InvalidOperationException("Repository tree is not available.");
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (string.IsNullOrWhiteSpace(stateRoot)) throw new ArgumentException("Value cannot be empty.", nameof(stateRoot));
            if (protectedPaths == null) throw new ArgumentNullException(nameof(protectedPaths));
            if (string.IsNullOrWhiteSpace(tempRootDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempRootDirectoryPath));

            return new SynchronizationContext
            {
                Repository = repository,
                TreeResult = treeResult,
                RemoteKind = remoteKind,
                TargetDirectoryPath = targetDirectoryPath,
                StateRoot = stateRoot,
                ProtectedPaths = protectedPaths,
                TempRootDirectoryPath = tempRootDirectoryPath,
                ActiveLog = activeLog
            };
        }

        private void PrepareWorkingState(SynchronizationContext context)
        {
            context.StateDirectoryPath = this.syncStateStore.GetRepositoryStateDirectory(context.StateRoot, context.Repository.StateKey);
            Directory.CreateDirectory(context.StateDirectoryPath);
            context.ManifestPath = this.syncStateStore.GetLegacyManifestPath(context.StateDirectoryPath);
        }

        private static void PrintRepositoryAccessReady(SynchronizationContext context)
        {
            Console.WriteLine("[1/4] Repository access ready...");
            Console.WriteLine(string.Format("       Branch: {0}", context.TreeResult.Branch));
            Console.WriteLine(string.Format("       Source: {0}", context.TreeResult.Source));
        }

        private void ClassifyRemoteEntries(SynchronizationContext context)
        {
            context.ImportedState = this.syncStateStore.Import(context.StateDirectoryPath);
            if (context.ImportedState.CacheUnreadable)
            {
                Console.WriteLine("       Sync cache unreadable. Rebuilding...");
            }

            context.CachedFiles = context.ImportedState.Files;
            context.NewCachedFiles = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);
            context.RemoteFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
            context.ExcludedFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
            context.SkippedConflictFiles = new List<string>();

            foreach (TreeEntry entry in context.TreeResult.Tree)
            {
                if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativePath = this.safePathService.NormalizeRelativePath(entry.path);
                if (SyncPolicy.IsExcludedRootFile(relativePath))
                {
                    context.ExcludedFiles[relativePath] = entry;
                }
                else if (SyncPolicy.IsAlwaysSkippedFile(relativePath))
                {
                    context.SkippedConflictFiles.Add(relativePath);
                }
                else
                {
                    context.RemoteFiles[relativePath] = entry;
                }
            }
        }

        private void RemoveExcludedRootFilesWhenSafe(SynchronizationContext context)
        {
            Console.WriteLine("[2/4] Removing repo README/LICENSE when safe...");
            foreach (string relativePath in SortKeys(context.ExcludedFiles.Keys))
            {
                TreeEntry entry = context.ExcludedFiles[relativePath];
                string destinationPath = this.safePathService.GetTargetPathFromRelative(context.TargetDirectoryPath, relativePath);
                string destinationFullPath = this.safePathService.GetFullPath(destinationPath);

                if (context.ProtectedPaths.Contains(destinationFullPath))
                {
                    WriteLogOnlyLine(context.ActiveLog, "Skipped protected README/LICENSE file: " + relativePath);
                    continue;
                }

                if (!File.Exists(destinationPath))
                {
                    continue;
                }

                this.safePathService.AssertSafeManagedPath(context.TargetDirectoryPath, destinationPath);

                bool matchesRemote = this.syncStateStore.MatchesCachedRemote(relativePath, destinationPath, entry, context.CachedFiles);
                if (!matchesRemote)
                {
                    matchesRemote = this.gitBlobHasher.MatchesRemoteBlob(destinationPath, entry);
                }

                if (matchesRemote)
                {
                    File.Delete(destinationPath);
                    this.safePathService.RemoveEmptyParentDirectories(destinationPath, context.TargetDirectoryPath);
                    context.Summary.ExcludedRemoved++;
                    WriteLogOnlyLine(context.ActiveLog, "Removed README/LICENSE file: " + relativePath);
                }
                else
                {
                    WriteLogOnlyLine(context.ActiveLog, "Kept local README/LICENSE file: " + relativePath);
                }
            }
        }

        private void LogSkippedConflictFiles(SynchronizationContext context)
        {
            foreach (string relativePath in SortKeys(context.SkippedConflictFiles))
            {
                context.Summary.SkippedConflictFiles.Add(relativePath);
                WriteLogOnlyLine(context.ActiveLog, "Skipped compile-only conflict file: " + relativePath);
            }
        }

        private void DownloadAndUpdateFiles(SynchronizationContext context)
        {
            Console.WriteLine("[3/4] Downloading and updating files...");
            context.NewManifest = new List<string>();
            List<string> sortedRemoteFiles = SortKeys(context.RemoteFiles.Keys);
            context.ProgressWriter = context.ActiveLog == null ? Console.Out : context.ActiveLog.ConsoleWriter;

            using (ProgressDisplay progress = new ProgressDisplay(context.ProgressWriter, ProgressDisplay.CanRefresh()))
            {
                for (int index = 0; index < sortedRemoteFiles.Count; index++)
                {
                    string relativePath = sortedRemoteFiles[index];
                    TreeEntry entry = context.RemoteFiles[relativePath];
                    string destinationPath = this.safePathService.GetTargetPathFromRelative(context.TargetDirectoryPath, relativePath);
                    string destinationFullPath = this.safePathService.GetFullPath(destinationPath);

                    progress.Update(
                        FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | checking", context.Summary.Added, context.Summary.Updated, context.Summary.Unchanged)),
                        FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));

                    if (context.ProtectedPaths.Contains(destinationFullPath))
                    {
                        WriteLogOnlyLine(context.ActiveLog, "Skipped protected updater file: " + relativePath);
                        continue;
                    }

                    context.NewManifest.Add(relativePath);
                    this.safePathService.AssertSafeManagedPath(context.TargetDirectoryPath, destinationPath);
                    this.safePathService.AssertNoDirectoryConflict(destinationPath);

                    if (this.syncStateStore.MatchesCachedRemote(relativePath, destinationPath, entry, context.CachedFiles))
                    {
                        context.NewCachedFiles[relativePath] = this.syncStateStore.CreateLocalFileState(relativePath, destinationPath, entry.sha);
                        context.Summary.Unchanged++;
                        WriteLogOnlyLine(context.ActiveLog, "Cached match: " + relativePath);
                        continue;
                    }

                    bool existed = File.Exists(destinationPath);
                    if (existed && this.gitBlobHasher.MatchesRemoteBlob(destinationPath, entry))
                    {
                        context.NewCachedFiles[relativePath] = this.syncStateStore.CreateLocalFileState(relativePath, destinationPath, entry.sha);
                        context.Summary.Unchanged++;
                        WriteLogOnlyLine(context.ActiveLog, "Verified match: " + relativePath);
                        continue;
                    }

                    progress.Update(
                        FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | downloading", context.Summary.Added, context.Summary.Updated, context.Summary.Unchanged)),
                        FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));

                    this.DownloadRemoteFile(context.Repository, context.TreeResult.Branch, entry, context.TempRootDirectoryPath, destinationPath, context.RemoteKind);
                    context.NewCachedFiles[relativePath] = this.syncStateStore.CreateLocalFileState(relativePath, destinationPath, entry.sha);

                    if (existed)
                    {
                        context.Summary.Updated++;
                        WriteLogOnlyLine(context.ActiveLog, "Updated: " + relativePath);
                    }
                    else
                    {
                        context.Summary.Added++;
                        WriteLogOnlyLine(context.ActiveLog, "Added: " + relativePath);
                    }
                }

                progress.Complete(
                    FormatProgressStatus("[3/4] Files", sortedRemoteFiles.Count, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2}", context.Summary.Added, context.Summary.Updated, context.Summary.Unchanged)),
                    FormatProgressBarLine(sortedRemoteFiles.Count, sortedRemoteFiles.Count));
            }
        }

        private void RemoveUpstreamDeletedFiles(SynchronizationContext context)
        {
            Console.WriteLine("[4/4] Removing files deleted upstream...");
            List<string> oldManifest = this.ReadOldManifest(context);

            HashSet<string> remoteSet = new HashSet<string>(context.NewManifest, StringComparer.OrdinalIgnoreCase);
            foreach (string relativePath in oldManifest)
            {
                if (remoteSet.Contains(relativePath) || SyncPolicy.IsAlwaysSkippedFile(relativePath))
                {
                    continue;
                }

                string destinationPath = this.safePathService.GetTargetPathFromRelative(context.TargetDirectoryPath, relativePath);
                string destinationFullPath = this.safePathService.GetFullPath(destinationPath);

                if (context.ProtectedPaths.Contains(destinationFullPath))
                {
                    WriteLogOnlyLine(context.ActiveLog, "Skipped protected stale file: " + relativePath);
                    continue;
                }

                if (!File.Exists(destinationPath))
                {
                    continue;
                }

                this.safePathService.AssertSafeManagedPath(context.TargetDirectoryPath, destinationPath);
                File.Delete(destinationPath);
                this.safePathService.RemoveEmptyParentDirectories(destinationPath, context.TargetDirectoryPath);
                context.Summary.Removed++;
                WriteLogOnlyLine(context.ActiveLog, "Removed upstream-deleted file: " + relativePath);
            }
        }

        private void PersistState(SynchronizationContext context)
        {
            List<string> sortedManifest = context.NewManifest.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllLines(context.ManifestPath, sortedManifest.ToArray(), new UTF8Encoding(false));
            this.syncStateStore.Export(context.StateDirectoryPath, sortedManifest, context.NewCachedFiles);
        }

        private List<string> ReadOldManifest(SynchronizationContext context)
        {
            List<string> oldManifest = new List<string>(context.ImportedState.TrackedFiles);
            if (oldManifest.Count == 0 && File.Exists(context.ManifestPath))
            {
                oldManifest = File.ReadAllLines(context.ManifestPath)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(this.safePathService.NormalizeRelativePath)
                    .ToList();
            }

            return oldManifest;
        }

        private void DownloadRemoteFile(RepositoryTarget repository, string branch, TreeEntry entry, string tempRootDirectoryPath, string destinationPath, RepositoryRemoteKind remoteKind)
        {
            string tempFilePath = this.remoteRepositoryClient.DownloadVerifiedFileToTemporaryPath(repository, branch, entry, tempRootDirectoryPath, remoteKind);
            try
            {
                this.atomicFileWriter.WriteFileAtomically(tempFilePath, destinationPath);
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void WriteLogOnlyLine(LogSession activeLog, string message)
        {
            if (activeLog == null)
            {
                return;
            }

            try
            {
                activeLog.WriteLogOnlyLine(message);
            }
            catch
            {
            }
        }

        private static List<string> SortKeys(IEnumerable<string> keys)
        {
            return keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string FormatProgressStatus(string stageLabel, int current, int total, string metrics)
        {
            return string.Format("{0} {1}/{2} | {3}", stageLabel, Math.Max(0, current), Math.Max(0, total), metrics);
        }

        private static string FormatProgressBarLine(int current, int total)
        {
            int safeCurrent = Math.Max(0, current);
            int safeTotal = Math.Max(0, total);
            string suffix = string.Format(" {0}/{1}", safeCurrent, safeTotal);
            int width = GetProgressBarWidth(suffix.Length);
            return BuildProgressBar(safeCurrent, safeTotal, width) + suffix;
        }

        private static string BuildProgressBar(int current, int total, int width)
        {
            int safeWidth = Math.Max(8, width);
            int safeCurrent = Math.Max(0, current);
            int safeTotal = Math.Max(0, total);
            int filled = safeTotal <= 0
                ? safeWidth
                : Math.Min(safeWidth, (int)Math.Round((double)Math.Min(safeCurrent, safeTotal) * safeWidth / safeTotal, MidpointRounding.AwayFromZero));

            return "[" + new string('#', filled) + new string('-', safeWidth - filled) + "]";
        }

        private static int GetProgressBarWidth(int suffixLength)
        {
            try
            {
                int lineWidth = Math.Max(20, Console.BufferWidth - 1);
                return Math.Max(8, lineWidth - suffixLength - 2);
            }
            catch
            {
                return 48;
            }
        }
    }
}
