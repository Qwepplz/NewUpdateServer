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

        public int Run(string[] args)
        {
            string targetDir = SyncPathUtility.GetFullPath(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            SyncMutexHandle mutexHandle = null;
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

                string targetHash = SyncPathUtility.GetTargetHash(targetDir);
                mutexHandle = SyncMutexHandle.Acquire(targetHash);

                HashSet<string> protectedPaths = SafePathService.BuildProtectedPathSet(targetDir);
                int staleArtifactsRemoved = SafePathService.RemoveStaleUpdaterArtifacts(targetDir, protectedPaths);
                if (staleArtifactsRemoved > 0)
                {
                    Console.WriteLine(string.Format("Cleaned leftover temp files: {0}", staleArtifactsRemoved));
                }

                string stateRoot = SyncPathUtility.GetStateDirectory(targetDir, targetHash);
                SyncSummary totalSummary = new SyncSummary();
                foreach (RepositoryTarget repository in selectedRepositories)
                {
                    totalSummary.Merge(repositorySynchronizer.Synchronize(repository, targetDir, stateRoot, protectedPaths));
                }

                Console.WriteLine();
                Console.WriteLine(selectedRepositories.Count > 1 ? "All selected syncs complete." : "Sync complete.");
                PrintSyncSummary(totalSummary);
                ConsoleUiHelper.PauseBeforeExit();
                return 0;
            }
            catch (Exception exception)
            {
                Console.WriteLine();
                Console.WriteLine("Sync failed.");
                Console.WriteLine(exception.Message);
                LoggingService.LogException(exception);
                ConsoleUiHelper.PauseBeforeExit();
                return 1;
            }
            finally
            {
                if (mutexHandle != null)
                {
                    mutexHandle.Dispose();
                }

                LoggingService.Shutdown();
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
