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
        public SyncSummary Synchronize(RepositoryTarget repository, string targetDir, string stateRoot, HashSet<string> protectedPaths)
    {
        string tempRoot = null;

        try
        {
            string repoStateDir = Path.Combine(stateRoot, repository.StateKey);
            Directory.CreateDirectory(repoStateDir);
            string manifestPath = Path.Combine(repoStateDir, "tracked-files.txt");
            string statePath = Path.Combine(repoStateDir, "sync-state.json");

            tempRoot = Path.Combine(Path.GetTempPath(), "PugGet5Sync_" + repository.StateKey + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            Console.WriteLine();
            Console.WriteLine(string.Format("=== {0}/{1} ({2}) ===", repository.GithubOwner, repository.GithubRepo, repository.DisplayName));
            Console.WriteLine("[1/4] Reading repository tree...");
            string defaultBranch = RemoteRepositoryClient.GetDefaultBranch(repository);
            TreeResult treeResult = RemoteRepositoryClient.GetRemoteTree(repository, new[] { defaultBranch, "main", "master" });
            Console.WriteLine(string.Format("       Branch: {0}", treeResult.Branch));
            Console.WriteLine(string.Format("       Source: {0}", treeResult.Source));

            ImportedState importedState = SyncStateStore.ImportSyncState(statePath, manifestPath);
            Dictionary<string, CachedFileState> cachedFiles = importedState.Files;
            Dictionary<string, CachedFileState> newCachedFiles = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, TreeEntry> remoteFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TreeEntry> excludedFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
            List<string> skippedConflictFiles = new List<string>();

            foreach (TreeEntry entry in treeResult.Tree)
            {
                if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativePath = SyncPathUtility.NormalizeRelativePath(entry.path);
                if (SyncPolicy.IsExcludedRootFile(relativePath))
                {
                    excludedFiles[relativePath] = entry;
                }
                else if (SyncPolicy.IsAlwaysSkippedFile(relativePath))
                {
                    skippedConflictFiles.Add(relativePath);
                }
                else
                {
                    remoteFiles[relativePath] = entry;
                }
            }

            Console.WriteLine("[2/4] Removing repo README/LICENSE when safe...");
            int excludedRemoved = 0;
            int excludedKept = 0;
            List<string> sortedExcludedFiles = SyncPathUtility.SortKeys(excludedFiles.Keys);
            for (int index = 0; index < sortedExcludedFiles.Count; index++)
            {
                string relativePath = sortedExcludedFiles[index];
                TreeEntry entry = excludedFiles[relativePath];
                string destinationPath = SyncPathUtility.GetTargetPathFromRelative(targetDir, relativePath);
                string destinationFull = SyncPathUtility.GetFullPath(destinationPath);

                if (protectedPaths.Contains(destinationFull))
                {
                    LoggingService.WriteLogOnlyLine("Skipped protected README/LICENSE file: " + relativePath);
                    continue;
                }

                if (!File.Exists(destinationPath))
                {
                    continue;
                }

                SafePathService.AssertSafeManagedPath(targetDir, destinationPath);

                bool matchesRemote = FileStateService.TestCachedRemoteMatch(relativePath, destinationPath, entry, cachedFiles);
                if (!matchesRemote)
                {
                    matchesRemote = FileStateService.TestLocalMatchesRemoteBlob(destinationPath, entry);
                }

                if (matchesRemote)
                {
                    File.Delete(destinationPath);
                    SafePathService.RemoveEmptyParentDirectories(destinationPath, targetDir);
                    excludedRemoved++;
                    LoggingService.WriteLogOnlyLine("Removed README/LICENSE file: " + relativePath);
                }
                else
                {
                    excludedKept++;
                    LoggingService.WriteLogOnlyLine("Kept local README/LICENSE file: " + relativePath);
                }
            }

            foreach (string relativePath in SyncPathUtility.SortKeys(skippedConflictFiles))
            {
                LoggingService.WriteLogOnlyLine("Skipped compile-only conflict file: " + relativePath);
            }

            Console.WriteLine("[3/4] Downloading and updating files...");
            int added = 0;
            int updated = 0;
            int unchanged = 0;
            List<string> newManifest = new List<string>();
            List<string> sortedRemoteFiles = SyncPathUtility.SortKeys(remoteFiles.Keys);
            using (ProgressDisplay progress = ConsoleUiHelper.CreateProgressDisplay())
            {
                for (int index = 0; index < sortedRemoteFiles.Count; index++)
                {
                    string relativePath = sortedRemoteFiles[index];
                    TreeEntry entry = remoteFiles[relativePath];
                    string destinationPath = SyncPathUtility.GetTargetPathFromRelative(targetDir, relativePath);
                    string destinationFull = SyncPathUtility.GetFullPath(destinationPath);
                    progress.Update(
                        ConsoleUiHelper.FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | checking", added, updated, unchanged)),
                        ConsoleUiHelper.FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));

                    if (protectedPaths.Contains(destinationFull))
                    {
                        LoggingService.WriteLogOnlyLine("Skipped protected updater file: " + relativePath);
                        continue;
                    }

                    newManifest.Add(relativePath);
                    SafePathService.AssertSafeManagedPath(targetDir, destinationPath);
                    SafePathService.AssertNoDirectoryConflict(destinationPath);

                    if (FileStateService.TestCachedRemoteMatch(relativePath, destinationPath, entry, cachedFiles))
                    {
                        newCachedFiles[relativePath] = FileStateService.GetLocalFileState(destinationPath, entry.sha);
                        unchanged++;
                        LoggingService.WriteLogOnlyLine("Cached match: " + relativePath);
                        continue;
                    }

                    bool existed = File.Exists(destinationPath);
                    if (existed && FileStateService.TestLocalMatchesRemoteBlob(destinationPath, entry))
                    {
                        newCachedFiles[relativePath] = FileStateService.GetLocalFileState(destinationPath, entry.sha);
                        unchanged++;
                        LoggingService.WriteLogOnlyLine("Verified match: " + relativePath);
                        continue;
                    }

                    string encodedPath = SyncPathUtility.ConvertToUrlPath(relativePath);
                    List<string> downloadUrls = RemoteRepositoryClient.BuildRepositoryRawUrls(repository, treeResult.Branch, encodedPath);

                    progress.Update(
                        ConsoleUiHelper.FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | downloading", added, updated, unchanged)),
                        ConsoleUiHelper.FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));
                    RemoteRepositoryClient.DownloadRemoteFile(downloadUrls, destinationPath, entry.sha, tempRoot);
                    newCachedFiles[relativePath] = FileStateService.GetLocalFileState(destinationPath, entry.sha);

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

            Console.WriteLine("[4/4] Removing files deleted upstream...");
            List<string> oldManifest = new List<string>(importedState.TrackedFiles);
            if (oldManifest.Count == 0 && File.Exists(manifestPath))
            {
                oldManifest = SyncPathUtility.ReadManifest(manifestPath);
            }

            HashSet<string> remoteSet = new HashSet<string>(newManifest, StringComparer.OrdinalIgnoreCase);
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

                string destinationPath = SyncPathUtility.GetTargetPathFromRelative(targetDir, relativePath);
                string destinationFull = SyncPathUtility.GetFullPath(destinationPath);

                if (protectedPaths.Contains(destinationFull))
                {
                    LoggingService.WriteLogOnlyLine("Skipped protected stale file: " + relativePath);
                    continue;
                }

                if (!File.Exists(destinationPath))
                {
                    continue;
                }

                SafePathService.AssertSafeManagedPath(targetDir, destinationPath);
                File.Delete(destinationPath);
                SafePathService.RemoveEmptyParentDirectories(destinationPath, targetDir);
                removed++;
                LoggingService.WriteLogOnlyLine("Removed upstream-deleted file: " + relativePath);
            }

            List<string> sortedManifest = newManifest.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllLines(manifestPath, sortedManifest.ToArray(), new UTF8Encoding(false));
            SyncStateStore.ExportSyncState(statePath, sortedManifest, newCachedFiles);

            return new SyncSummary
            {
                Added = added,
                Updated = updated,
                Removed = removed,
                ExcludedRemoved = excludedRemoved,
                Unchanged = unchanged,
                SkippedConflictFiles = new HashSet<string>(skippedConflictFiles, StringComparer.OrdinalIgnoreCase)
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempRoot) && Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, true);
                }
                catch
                {
                }
            }
        }
    }
    }
}
