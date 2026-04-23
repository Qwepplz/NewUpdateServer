using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UpdateServer.Config;
using UpdateServer.ConsoleUi;
using ConsoleUiHelper = UpdateServer.ConsoleUi.ConsoleUi;
using UpdateServer.FileSystem;
using UpdateServer.Logging;
using UpdateServer.Remote;
using UpdateServer.Remote.Models;
using UpdateServer.State;

namespace UpdateServer.Sync
{
    internal sealed class RepositorySynchronizer
    {
        private sealed class RepositorySyncContext
        {
            public RepositorySyncContext(
                RepositoryTarget repository,
                TreeResult treeResult,
                RepositoryRemoteKind remoteKind,
                string targetDir,
                string stateRoot,
                HashSet<string> protectedPaths,
                string tempRootDirectoryPath)
            {
                Repository = repository;
                TreeResult = treeResult;
                RemoteKind = remoteKind;
                TargetDir = targetDir;
                StateRoot = stateRoot;
                ProtectedPaths = protectedPaths;
                TempRootDirectoryPath = tempRootDirectoryPath;
                RepoStateDir = Path.Combine(stateRoot, repository.StateKey);
                ManifestPath = Path.Combine(RepoStateDir, "tracked-files.txt");
                StatePath = Path.Combine(RepoStateDir, "sync-state.json");
                CachedFiles = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);
                NewCachedFiles = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);
                RemoteFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
                ExcludedFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
                SkippedConflictFiles = new List<string>();
                NewManifest = new List<string>();
                ExcludedRemovalResult = new ExcludedRemovalResult(0, 0);
                DownloadResult = new DownloadResult(0, 0, 0);
            }

            public RepositoryTarget Repository { get; private set; }

            public TreeResult TreeResult { get; private set; }

            public RepositoryRemoteKind RemoteKind { get; private set; }

            public string TargetDir { get; private set; }

            public string StateRoot { get; private set; }

            public HashSet<string> ProtectedPaths { get; private set; }

            public string TempRootDirectoryPath { get; private set; }

            public string RepoStateDir { get; private set; }

            public string ManifestPath { get; private set; }

            public string StatePath { get; private set; }

            public ImportedState ImportedState { get; set; }

            public Dictionary<string, CachedFileState> CachedFiles { get; set; }

            public Dictionary<string, CachedFileState> NewCachedFiles { get; private set; }

            public Dictionary<string, TreeEntry> RemoteFiles { get; private set; }

            public Dictionary<string, TreeEntry> ExcludedFiles { get; private set; }

            public List<string> SkippedConflictFiles { get; private set; }

            public List<string> NewManifest { get; private set; }

            public ExcludedRemovalResult ExcludedRemovalResult { get; set; }

            public DownloadResult DownloadResult { get; set; }

            public int Removed { get; set; }
        }

        private sealed class ExcludedRemovalResult
        {
            public ExcludedRemovalResult(int removed, int kept)
            {
                Removed = removed;
                Kept = kept;
            }

            public int Removed { get; private set; }

            public int Kept { get; private set; }
        }

        private sealed class DownloadResult
        {
            public DownloadResult(int added, int updated, int unchanged)
            {
                Added = added;
                Updated = updated;
                Unchanged = unchanged;
            }

            public int Added { get; private set; }

            public int Updated { get; private set; }

            public int Unchanged { get; private set; }
        }

        public SyncSummary Synchronize(
            RepositoryTarget repository,
            TreeResult treeResult,
            RepositoryRemoteKind remoteKind,
            string targetDir,
            string stateRoot,
            HashSet<string> protectedPaths,
            string tempRootDirectoryPath)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (treeResult == null) throw new ArgumentNullException(nameof(treeResult));
            if (treeResult.Tree == null) throw new InvalidOperationException("Repository tree is not available.");
            if (string.IsNullOrWhiteSpace(targetDir)) throw new ArgumentException("Value cannot be empty.", nameof(targetDir));
            if (string.IsNullOrWhiteSpace(stateRoot)) throw new ArgumentException("Value cannot be empty.", nameof(stateRoot));
            if (protectedPaths == null) throw new ArgumentNullException(nameof(protectedPaths));
            if (string.IsNullOrWhiteSpace(tempRootDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempRootDirectoryPath));

            RepositorySyncContext context = CreateContext(
                repository,
                treeResult,
                remoteKind,
                targetDir,
                stateRoot,
                protectedPaths,
                tempRootDirectoryPath);

            PrintRepositoryAccessReady(context);
            ClassifyTreeEntries(context);
            context.ExcludedRemovalResult = RemoveExcludedFilesWhenSafe(context);
            LogSkippedConflictFiles(context);
            context.DownloadResult = DownloadAndUpdateFiles(context);
            context.Removed = RemoveUpstreamDeletedFiles(context);
            PersistState(context);
            return BuildSummary(context);
        }

        private static RepositorySyncContext CreateContext(
            RepositoryTarget repository,
            TreeResult treeResult,
            RepositoryRemoteKind remoteKind,
            string targetDir,
            string stateRoot,
            HashSet<string> protectedPaths,
            string tempRootDirectoryPath)
        {
            RepositorySyncContext context = new RepositorySyncContext(
                repository,
                treeResult,
                remoteKind,
                targetDir,
                stateRoot,
                protectedPaths,
                tempRootDirectoryPath);
            Directory.CreateDirectory(context.RepoStateDir);
            return context;
        }

        private static void PrintRepositoryAccessReady(RepositorySyncContext context)
        {
            Console.WriteLine("[1/4] Repository access ready...");
            Console.WriteLine(string.Format("       Branch: {0}", context.TreeResult.Branch));
            Console.WriteLine(string.Format("       Source: {0}", context.TreeResult.Source));
        }

        private static void ClassifyTreeEntries(RepositorySyncContext context)
        {
            context.ImportedState = SyncStateStore.ImportSyncState(context.StatePath, context.ManifestPath);
            context.CachedFiles = context.ImportedState.Files;

            foreach (TreeEntry entry in context.TreeResult.Tree)
            {
                if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativePath = SyncPathUtility.NormalizeRelativePath(entry.path);
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

        private static ExcludedRemovalResult RemoveExcludedFilesWhenSafe(RepositorySyncContext context)
        {
            Console.WriteLine("[2/4] Removing repo README/LICENSE when safe...");
            int removed = 0;
            int kept = 0;
            List<string> sortedExcludedFiles = SyncPathUtility.SortKeys(context.ExcludedFiles.Keys);
            for (int index = 0; index < sortedExcludedFiles.Count; index++)
            {
                string relativePath = sortedExcludedFiles[index];
                TreeEntry entry = context.ExcludedFiles[relativePath];
                string destinationPath = SyncPathUtility.GetTargetPathFromRelative(context.TargetDir, relativePath);
                string destinationFull = SyncPathUtility.GetFullPath(destinationPath);

                if (context.ProtectedPaths.Contains(destinationFull))
                {
                    LoggingService.WriteLogOnlyLine("Skipped protected README/LICENSE file: " + relativePath);
                    continue;
                }

                if (!File.Exists(destinationPath))
                {
                    continue;
                }

                SafePathService.AssertSafeManagedPath(context.TargetDir, destinationPath);

                bool matchesRemote = FileStateService.TestCachedRemoteMatch(relativePath, destinationPath, entry, context.CachedFiles);
                if (!matchesRemote)
                {
                    matchesRemote = FileStateService.TestLocalMatchesRemoteBlob(destinationPath, entry);
                }

                if (matchesRemote)
                {
                    File.Delete(destinationPath);
                    SafePathService.RemoveEmptyParentDirectories(destinationPath, context.TargetDir);
                    removed++;
                    LoggingService.WriteLogOnlyLine("Removed README/LICENSE file: " + relativePath);
                }
                else
                {
                    kept++;
                    LoggingService.WriteLogOnlyLine("Kept local README/LICENSE file: " + relativePath);
                }
            }

            return new ExcludedRemovalResult(removed, kept);
        }

        private static void LogSkippedConflictFiles(RepositorySyncContext context)
        {
            foreach (string relativePath in SyncPathUtility.SortKeys(context.SkippedConflictFiles))
            {
                LoggingService.WriteLogOnlyLine("Skipped compile-only conflict file: " + relativePath);
            }
        }

        private static DownloadResult DownloadAndUpdateFiles(RepositorySyncContext context)
        {
            Console.WriteLine("[3/4] Downloading and updating files...");
            int added = 0;
            int updated = 0;
            int unchanged = 0;
            List<string> sortedRemoteFiles = SyncPathUtility.SortKeys(context.RemoteFiles.Keys);
            using (ProgressDisplay progress = ConsoleUiHelper.CreateProgressDisplay())
            {
                for (int index = 0; index < sortedRemoteFiles.Count; index++)
                {
                    string relativePath = sortedRemoteFiles[index];
                    TreeEntry entry = context.RemoteFiles[relativePath];
                    string destinationPath = SyncPathUtility.GetTargetPathFromRelative(context.TargetDir, relativePath);
                    string destinationFull = SyncPathUtility.GetFullPath(destinationPath);
                    progress.Update(
                        ConsoleUiHelper.FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | checking", added, updated, unchanged)),
                        ConsoleUiHelper.FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));

                    if (context.ProtectedPaths.Contains(destinationFull))
                    {
                        LoggingService.WriteLogOnlyLine("Skipped protected updater file: " + relativePath);
                        continue;
                    }

                    context.NewManifest.Add(relativePath);
                    SafePathService.AssertSafeManagedPath(context.TargetDir, destinationPath);
                    SafePathService.AssertNoDirectoryConflict(destinationPath);

                    if (FileStateService.TestCachedRemoteMatch(relativePath, destinationPath, entry, context.CachedFiles))
                    {
                        context.NewCachedFiles[relativePath] = FileStateService.GetLocalFileState(destinationPath, entry.sha);
                        unchanged++;
                        LoggingService.WriteLogOnlyLine("Cached match: " + relativePath);
                        continue;
                    }

                    bool existed = File.Exists(destinationPath);
                    if (existed && FileStateService.TestLocalMatchesRemoteBlob(destinationPath, entry))
                    {
                        context.NewCachedFiles[relativePath] = FileStateService.GetLocalFileState(destinationPath, entry.sha);
                        unchanged++;
                        LoggingService.WriteLogOnlyLine("Verified match: " + relativePath);
                        continue;
                    }

                    progress.Update(
                        ConsoleUiHelper.FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | downloading", added, updated, unchanged)),
                        ConsoleUiHelper.FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));
                    DownloadRemoteFile(context.Repository, context.TreeResult.Branch, entry, context.TempRootDirectoryPath, destinationPath, context.RemoteKind);
                    context.NewCachedFiles[relativePath] = FileStateService.GetLocalFileState(destinationPath, entry.sha);

                    if (existed)
                    {
                        updated++;
                        LoggingService.WriteLogOnlyLine("Updated: " + relativePath);
                    }
                    else
                    {
                        added++;
                        LoggingService.WriteLogOnlyLine("Added: " + relativePath);
                    }
                }

                progress.Complete(
                    ConsoleUiHelper.FormatProgressStatus("[3/4] Files", sortedRemoteFiles.Count, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2}", added, updated, unchanged)),
                    ConsoleUiHelper.FormatProgressBarLine(sortedRemoteFiles.Count, sortedRemoteFiles.Count));
            }

            return new DownloadResult(added, updated, unchanged);
        }

        private static int RemoveUpstreamDeletedFiles(RepositorySyncContext context)
        {
            Console.WriteLine("[4/4] Removing files deleted upstream...");
            List<string> oldManifest = new List<string>(context.ImportedState.TrackedFiles);
            if (oldManifest.Count == 0 && File.Exists(context.ManifestPath))
            {
                oldManifest = SyncPathUtility.ReadManifest(context.ManifestPath);
            }

            HashSet<string> remoteSet = new HashSet<string>(context.NewManifest, StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            for (int index = 0; index < oldManifest.Count; index++)
            {
                string relativePath = oldManifest[index];
                if (remoteSet.Contains(relativePath))
                {
                    continue;
                }

                if (SyncPolicy.IsAlwaysSkippedFile(relativePath))
                {
                    continue;
                }

                string destinationPath = SyncPathUtility.GetTargetPathFromRelative(context.TargetDir, relativePath);
                string destinationFull = SyncPathUtility.GetFullPath(destinationPath);

                if (context.ProtectedPaths.Contains(destinationFull))
                {
                    LoggingService.WriteLogOnlyLine("Skipped protected stale file: " + relativePath);
                    continue;
                }

                if (!File.Exists(destinationPath))
                {
                    continue;
                }

                SafePathService.AssertSafeManagedPath(context.TargetDir, destinationPath);
                File.Delete(destinationPath);
                SafePathService.RemoveEmptyParentDirectories(destinationPath, context.TargetDir);
                removed++;
                LoggingService.WriteLogOnlyLine("Removed upstream-deleted file: " + relativePath);
            }

            return removed;
        }

        private static void PersistState(RepositorySyncContext context)
        {
            List<string> sortedManifest = context.NewManifest.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllLines(context.ManifestPath, sortedManifest.ToArray(), new UTF8Encoding(false));
            SyncStateStore.ExportSyncState(context.StatePath, sortedManifest, context.NewCachedFiles);
        }

        private static SyncSummary BuildSummary(RepositorySyncContext context)
        {
            return new SyncSummary
            {
                Added = context.DownloadResult.Added,
                Updated = context.DownloadResult.Updated,
                Removed = context.Removed,
                ExcludedRemoved = context.ExcludedRemovalResult.Removed,
                Unchanged = context.DownloadResult.Unchanged,
                SkippedConflictFiles = new HashSet<string>(context.SkippedConflictFiles, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void DownloadRemoteFile(
            RepositoryTarget repository,
            string branch,
            TreeEntry entry,
            string tempRootDirectoryPath,
            string destinationPath,
            RepositoryRemoteKind remoteKind)
        {
            string tempFilePath = RemoteRepositoryClient.DownloadVerifiedFileToTemporaryPath(repository, branch, entry, tempRootDirectoryPath, remoteKind);
            try
            {
                SafePathService.WriteFileAtomically(tempFilePath, destinationPath);
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
    }
}
