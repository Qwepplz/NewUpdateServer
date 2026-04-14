using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using UpdateServer.Common;
using UpdateServer.Domain;
using UpdateServer.Features.Sync;
using UpdateServer.Infrastructure.Logging;
using UpdateServer.Infrastructure.Safety;
using UpdateServer.Presentation;

namespace UpdateServer
{
    internal sealed class UpdateServerHost
    {
        private readonly RepositorySyncService repositorySyncService = new RepositorySyncService();

public int Run(string[] args)
    {
        string targetDir = SyncPathUtility.GetFullPath(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        SyncMutexHandle mutexHandle = null;
        LoggingService.TryInitialize(targetDir, args);

        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            List<RepositoryTarget> selectedRepositories = ConsoleUi.ShowStartupPrompt(targetDir);
            if (selectedRepositories.Count == 0)
            {
                ConsoleUi.PauseBeforeExit();
                return 0;
            }

            string targetHash = SyncPathUtility.GetTargetHash(targetDir);
            mutexHandle = SyncMutexHandle.Acquire(targetHash);

            HashSet<string> protectedPaths = ManagedPathService.BuildProtectedPathSet(targetDir);
            int staleArtifactsRemoved = ManagedPathService.RemoveStaleUpdaterArtifacts(targetDir, protectedPaths);
            if (staleArtifactsRemoved > 0)
            {
                Console.WriteLine(string.Format("Cleaned leftover temp files: {0}", staleArtifactsRemoved));
            }

            string stateRoot = SyncPathUtility.GetStateDirectory(targetDir, targetHash);
            SyncSummary totalSummary = new SyncSummary();
            foreach (RepositoryTarget repository in selectedRepositories)
            {
                totalSummary.Merge(repositorySyncService.SyncRepository(repository, targetDir, stateRoot, protectedPaths));
            }

            Console.WriteLine();
            Console.WriteLine(selectedRepositories.Count > 1 ? "All selected syncs complete." : "Sync complete.");
            PrintSyncSummary(totalSummary);
            ConsoleUi.PauseBeforeExit();
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine();
            Console.WriteLine("Sync failed.");
            Console.WriteLine(exception.Message);
            LoggingService.LogException(exception);
            ConsoleUi.PauseBeforeExit();
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
