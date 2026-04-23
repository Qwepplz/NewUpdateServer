using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using UpdateServer.Config;
using UpdateServer.ConsoleUi;
using ConsoleUiHelper = UpdateServer.ConsoleUi.ConsoleUi;
using UpdateServer.FileSystem;
using UpdateServer.Logging;
using UpdateServer.Sync;

namespace UpdateServer.App
{
    internal sealed class UpdateServerApplication
    {
        private readonly RepositorySynchronizer repositorySynchronizer = new RepositorySynchronizer();

        private sealed class RunContext
        {
            private RunContext(string targetDir, string targetHash, string tempRootDirectoryPath, string stateRoot, HashSet<string> protectedPaths)
            {
                TargetDir = targetDir;
                TargetHash = targetHash;
                TempRootDirectoryPath = tempRootDirectoryPath;
                StateRoot = stateRoot;
                ProtectedPaths = protectedPaths;
            }

            public string TargetDir { get; private set; }

            public string TargetHash { get; private set; }

            public string TempRootDirectoryPath { get; private set; }

            public string StateRoot { get; private set; }

            public HashSet<string> ProtectedPaths { get; private set; }

            public static RunContext Create(string targetDir)
            {
                string targetHash = SyncPathUtility.GetTargetHash(targetDir);
                HashSet<string> protectedPaths = SafePathService.BuildProtectedPathSet(targetDir);
                string tempRootDirectoryPath = Path.Combine(Path.GetTempPath(), "PugGet5Sync_" + Guid.NewGuid().ToString("N"));
                string stateRoot = SyncPathUtility.GetStateDirectory(targetDir, targetHash);

                return new RunContext(targetDir, targetHash, tempRootDirectoryPath, stateRoot, protectedPaths);
            }
        }

        public int Run(string[] args)
        {
            string targetDir = SyncPathUtility.GetFullPath(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            SyncMutexHandle mutexHandle = null;
            RunContext runContext = null;
            LoggingService.TryInitialize(targetDir, args);

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                List<RepositoryTarget> selectedRepositories = ConsoleUiHelper.ShowStartupPrompt(targetDir);
                if (selectedRepositories.Count == 0)
                {
                    ConsoleUiHelper.PauseBeforeExit();
                    return 0;
                }

                runContext = RunContext.Create(targetDir);
                mutexHandle = SyncMutexHandle.Acquire(runContext.TargetHash);
                ReportStaleArtifacts(runContext.TargetDir, runContext.ProtectedPaths);
                Directory.CreateDirectory(runContext.TempRootDirectoryPath);

                Tuple<SyncSummary, int> synchronizationResult = SynchronizeSelectedRepositories(selectedRepositories, runContext);

                return CompleteRun(synchronizationResult.Item1, synchronizationResult.Item2);
            }
            catch (Exception exception)
            {
                return FailRun(exception);
            }
            finally
            {
                if (mutexHandle != null)
                {
                    mutexHandle.Dispose();
                }

                CleanupRun(runContext);
            }
        }

        private static void ReportStaleArtifacts(string targetDir, HashSet<string> protectedPaths)
        {
            int staleArtifactsRemoved = SafePathService.RemoveStaleUpdaterArtifacts(targetDir, protectedPaths);
            if (staleArtifactsRemoved > 0)
            {
                Console.WriteLine(string.Format("Cleaned leftover temp files: {0}", staleArtifactsRemoved));
            }
        }

        private Tuple<SyncSummary, int> SynchronizeSelectedRepositories(List<RepositoryTarget> selectedRepositories, RunContext runContext)
        {
            string stateRoot = runContext.StateRoot;
            SyncSummary totalSummary = new SyncSummary();
            int synchronizedRepositoryCount = 0;

            foreach (RepositoryTarget repository in selectedRepositories)
            {
                Console.WriteLine();
                Console.WriteLine(string.Format("=== {0}/{1} ({2}) ===", repository.GithubOwner, repository.GithubRepo, repository.DisplayName));

                UpdateServer.Remote.Models.TreeResult preparedTree;
                UpdateServer.Remote.RepositoryRemoteKind remoteKind;
                if (!TryPrepareRepositoryTree(repository, runContext.TempRootDirectoryPath, out preparedTree, out remoteKind))
                {
                    continue;
                }

                totalSummary.Merge(repositorySynchronizer.Synchronize(repository, preparedTree, remoteKind, runContext.TargetDir, stateRoot, runContext.ProtectedPaths, runContext.TempRootDirectoryPath));
                synchronizedRepositoryCount++;
            }

            return Tuple.Create(totalSummary, synchronizedRepositoryCount);
        }

        private static int CompleteRun(SyncSummary totalSummary, int synchronizedRepositoryCount)
        {
            Console.WriteLine();
            if (synchronizedRepositoryCount == 0)
            {
                Console.WriteLine("No repositories were synchronized.");
                ConsoleUiHelper.PauseBeforeExit();
                return 0;
            }

            Console.WriteLine(synchronizedRepositoryCount > 1 ? "Selected syncs complete." : "Sync complete.");
            PrintSyncSummary(totalSummary);
            ConsoleUiHelper.PauseBeforeExit();
            return 0;
        }

        private static int FailRun(Exception exception)
        {
            Console.WriteLine();
            Console.WriteLine("Sync failed.");
            Console.WriteLine(exception.Message);
            LoggingService.LogException(exception);
            ConsoleUiHelper.PauseBeforeExit();
            return 1;
        }

        private static void CleanupRun(RunContext runContext)
        {
            if (runContext != null && !string.IsNullOrWhiteSpace(runContext.TempRootDirectoryPath) && Directory.Exists(runContext.TempRootDirectoryPath))
            {
                try
                {
                    Directory.Delete(runContext.TempRootDirectoryPath, true);
                }
                catch
                {
                }
            }

            LoggingService.Shutdown();
        }

        private static bool TryPrepareRepositoryTree(
            RepositoryTarget repository,
            string tempRootDirectoryPath,
            out UpdateServer.Remote.Models.TreeResult treeResult,
            out UpdateServer.Remote.RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(tempRootDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempRootDirectoryPath));

            treeResult = null;
            remoteKind = UpdateServer.Remote.RepositoryRemoteKind.Github;

            try
            {
                treeResult = UpdateServer.Remote.RemoteRepositoryClient.PrepareRepositoryTree(repository, tempRootDirectoryPath, remoteKind);
                LoggingService.WriteLogOnlyLine("Selected remote source for " + repository.DisplayName + ": GitHub.");
                return true;
            }
            catch (Exception githubException)
            {
                LoggingService.WriteLogOnlyLine("GitHub sync preparation failed for " + repository.DisplayName + ":");
                LoggingService.WriteLogOnlyLine(githubException.ToString());

                if (!repository.HasMirror)
                {
                    throw;
                }

                if (!ConsoleUiHelper.ShowMirrorConfirmation(repository, githubException.Message))
                {
                    Console.WriteLine(string.Format("Skipped sync for {0}.", repository.DisplayName));
                    LoggingService.WriteLogOnlyLine("Mirror sync canceled by user for " + repository.DisplayName + ".");
                    return false;
                }

                remoteKind = UpdateServer.Remote.RepositoryRemoteKind.Mirror;
                treeResult = UpdateServer.Remote.RemoteRepositoryClient.PrepareRepositoryTree(repository, tempRootDirectoryPath, remoteKind);
                LoggingService.WriteLogOnlyLine("Selected remote source for " + repository.DisplayName + ": Gitee mirror.");
                return true;
            }
        }

        private static void PrintSyncSummary(SyncSummary summary)
        {
            Console.WriteLine(string.Format("Added: {0}", summary.Added));
            Console.WriteLine(string.Format("Updated: {0}", summary.Updated));
            Console.WriteLine(string.Format("Removed: {0}", summary.Removed));
            Console.WriteLine(string.Format("Unchanged: {0}", summary.Unchanged));
        }
    }
}
