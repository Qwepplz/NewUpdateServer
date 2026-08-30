using System;
using System.Collections.Generic;
using System.IO;
using UpdateServer.Config;
using UpdateServer.ConsoleUi;
using UpdateServer.FileSystem;
using UpdateServer.Logging;
using UpdateServer.Remote;
using UpdateServer.Remote.Models;
using UpdateServer.Security;
using UpdateServer.State;
using UpdateServer.Sync;

namespace UpdateServer.App
{
    internal sealed class UpdateServerApplication
    {
        private readonly StartupMenu startupMenu;
        private readonly ISafePathService safePathService;
        private readonly ISyncStateStore syncStateStore;
        private readonly IRemoteRepositoryClient remoteRepositoryClient;
        private readonly IRepositorySynchronizer repositorySynchronizer;
        private LogSession activeLog;

        private sealed class RunContext
        {
            private RunContext(string targetDirectoryPath, string targetHash, string stateRoot, HashSet<string> protectedPaths)
            {
                this.TargetDirectoryPath = targetDirectoryPath;
                this.TargetHash = targetHash;
                this.StateRoot = stateRoot;
                this.ProtectedPaths = protectedPaths;
            }

            internal string TargetDirectoryPath { get; private set; }

            internal string TargetHash { get; private set; }

            internal string StateRoot { get; private set; }

            internal HashSet<string> ProtectedPaths { get; private set; }

            internal string TempRootDirectoryPath { get; private set; }

            internal static RunContext Create(string targetDirectoryPath, ISafePathService safePathService, ISyncStateStore syncStateStore)
            {
                if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));
                if (syncStateStore == null) throw new ArgumentNullException(nameof(syncStateStore));

                string normalizedTargetDirectoryPath = safePathService.GetFullPath(targetDirectoryPath);
                string targetHash = syncStateStore.GetTargetHash(normalizedTargetDirectoryPath);
                HashSet<string> protectedPaths = safePathService.BuildProtectedPathSet(normalizedTargetDirectoryPath);
                string stateRoot = syncStateStore.GetStateRoot(normalizedTargetDirectoryPath, targetHash);

                return new RunContext(normalizedTargetDirectoryPath, targetHash, stateRoot, protectedPaths);
            }

            internal void CreateTempRootDirectory()
            {
                this.TempRootDirectoryPath = Path.Combine(Path.GetTempPath(), SyncConfiguration.TempRootDirectoryPrefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(this.TempRootDirectoryPath);
            }
        }

        public UpdateServerApplication()
        {
            ISafePathService safePathService = new SafePathService();
            IGitBlobHasher gitBlobHasher = new GitBlobHasher();
            IRepositoryUrlBuilder repositoryUrlBuilder = new RepositoryUrlBuilder();
            IRemoteRepositoryClient remoteRepositoryClient = new RemoteRepositoryClient(repositoryUrlBuilder, gitBlobHasher);
            IAtomicFileWriter atomicFileWriter = new AtomicFileWriter();
            ISyncStateStore syncStateStore = new SyncStateStore(safePathService);
            IRepositorySynchronizer repositorySynchronizer = new RepositorySynchronizer(
                remoteRepositoryClient,
                safePathService,
                atomicFileWriter,
                syncStateStore,
                gitBlobHasher);

            this.startupMenu = new StartupMenu();
            this.safePathService = safePathService;
            this.syncStateStore = syncStateStore;
            this.remoteRepositoryClient = remoteRepositoryClient;
            this.repositorySynchronizer = repositorySynchronizer;
        }

        public UpdateServerApplication(
            StartupMenu startupMenu,
            ISafePathService safePathService,
            ISyncStateStore syncStateStore,
            IRemoteRepositoryClient remoteRepositoryClient,
            IRepositorySynchronizer repositorySynchronizer)
        {
            if (startupMenu == null) throw new ArgumentNullException(nameof(startupMenu));
            if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));
            if (syncStateStore == null) throw new ArgumentNullException(nameof(syncStateStore));
            if (remoteRepositoryClient == null) throw new ArgumentNullException(nameof(remoteRepositoryClient));
            if (repositorySynchronizer == null) throw new ArgumentNullException(nameof(repositorySynchronizer));

            this.startupMenu = startupMenu;
            this.safePathService = safePathService;
            this.syncStateStore = syncStateStore;
            this.remoteRepositoryClient = remoteRepositoryClient;
            this.repositorySynchronizer = repositorySynchronizer;
        }

        public int Run(string[] args)
        {
            string targetDirectoryPath = this.safePathService.GetFullPath(
                AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            RunContext context = null;
            SyncMutexHandle mutexHandle = null;
            this.TryInitializeLogging(targetDirectoryPath, args);

            try
            {
                if (!this.startupMenu.ShowStartupPrompt(targetDirectoryPath))
                {
                    return this.ExitWithoutSynchronization();
                }

                context = RunContext.Create(targetDirectoryPath, this.safePathService, this.syncStateStore);
                mutexHandle = SyncMutexHandle.Acquire(context.TargetHash);
                this.ReportStaleArtifacts(context);
                context.CreateTempRootDirectory();

                SyncSummary syncSummary = this.TrySynchronizeRepository(RepositoryCatalog.Get5Repository, context);
                return this.CompleteRun(syncSummary);
            }
            catch (Exception exception)
            {
                return this.FailRun(exception);
            }
            finally
            {
                if (mutexHandle != null)
                {
                    mutexHandle.Dispose();
                }

                CleanupRun(context);
                this.ShutdownLogging();
            }
        }

        private int ExitWithoutSynchronization()
        {
            this.startupMenu.PauseBeforeExit();
            return 0;
        }

        private void ReportStaleArtifacts(RunContext context)
        {
            int staleArtifactsRemoved = this.safePathService.RemoveStaleUpdaterArtifacts(context.TargetDirectoryPath, context.ProtectedPaths);
            if (staleArtifactsRemoved > 0)
            {
                Console.WriteLine(string.Format("Cleaned leftover temp files: {0}", staleArtifactsRemoved));
            }
        }

        private SyncSummary TrySynchronizeRepository(RepositoryTarget repository, RunContext context)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (context == null) throw new ArgumentNullException(nameof(context));

            Console.WriteLine();
            Console.WriteLine(string.Format("=== {0}/{1} ({2}) ===", repository.GithubOwner, repository.GithubRepo, repository.DisplayName));

            TreeResult preparedTree;
            RepositoryRemoteKind remoteKind;
            if (!this.TryPrepareRepositoryTree(repository, context.TempRootDirectoryPath, out preparedTree, out remoteKind))
            {
                return null;
            }

            return this.repositorySynchronizer.Synchronize(
                repository,
                preparedTree,
                remoteKind,
                context.TargetDirectoryPath,
                context.StateRoot,
                context.ProtectedPaths,
                context.TempRootDirectoryPath,
                this.activeLog);
        }

        private int CompleteRun(SyncSummary syncSummary)
        {
            Console.WriteLine();
            if (syncSummary == null)
            {
                Console.WriteLine("Get5 sync was not completed.");
                this.startupMenu.PauseBeforeExit();
                return 0;
            }

            Console.WriteLine("Sync complete.");
            Console.WriteLine(string.Format("Added: {0}", syncSummary.Added));
            Console.WriteLine(string.Format("Updated: {0}", syncSummary.Updated));
            Console.WriteLine(string.Format("Removed: {0}", syncSummary.Removed));
            Console.WriteLine(string.Format("Unchanged: {0}", syncSummary.Unchanged));
            this.startupMenu.PauseBeforeExit();
            return 0;
        }

        private int FailRun(Exception exception)
        {
            Console.WriteLine();
            Console.WriteLine("Sync failed.");
            Console.WriteLine(exception.Message);
            this.LogException(exception);
            this.startupMenu.PauseBeforeExit();
            return 1;
        }

        private static void CleanupRun(RunContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.TempRootDirectoryPath) || !Directory.Exists(context.TempRootDirectoryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(context.TempRootDirectoryPath, true);
            }
            catch
            {
            }
        }

        private bool TryPrepareRepositoryTree(RepositoryTarget repository, string tempRootDirectoryPath, out TreeResult treeResult, out RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(tempRootDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempRootDirectoryPath));

            treeResult = null;
            remoteKind = RepositoryRemoteKind.Github;

            try
            {
                treeResult = this.remoteRepositoryClient.PrepareRepositoryTree(repository, tempRootDirectoryPath, remoteKind);
                this.WriteLogOnlyLine("Selected remote source for " + repository.DisplayName + ": GitHub.");
                return true;
            }
            catch (Exception githubException)
            {
                this.WriteLogOnlyLine("GitHub sync preparation failed for " + repository.DisplayName + ":");
                this.WriteLogOnlyLine(githubException.ToString());

                if (!repository.HasMirror)
                {
                    throw;
                }

                if (!this.startupMenu.ShowMirrorConfirmation(repository, githubException.Message))
                {
                    Console.WriteLine(string.Format("Skipped sync for {0}.", repository.DisplayName));
                    this.WriteLogOnlyLine("Mirror sync canceled by user for " + repository.DisplayName + ".");
                    return false;
                }

                remoteKind = RepositoryRemoteKind.Mirror;
                treeResult = this.remoteRepositoryClient.PrepareRepositoryTree(repository, tempRootDirectoryPath, remoteKind);
                this.WriteLogOnlyLine("Selected remote source for " + repository.DisplayName + ": Gitee mirror.");
                return true;
            }
        }

        private void TryInitializeLogging(string targetDirectoryPath, string[] args)
        {
            if (this.activeLog != null)
            {
                return;
            }

            try
            {
                this.activeLog = LogSession.Create(targetDirectoryPath, this.safePathService);
                this.activeLog.Attach();
                this.activeLog.WriteSessionStart(targetDirectoryPath, args);
                Console.WriteLine(string.Format("Log file: {0}", this.activeLog.CurrentLogPath));
                LogArchiveService.TryArchivePreviousLogs(
                    targetDirectoryPath,
                    this.activeLog.CurrentLogPath,
                    this.safePathService,
                    this.activeLog.WriteLogOnlyLine);
            }
            catch
            {
                if (this.activeLog != null)
                {
                    try
                    {
                        this.activeLog.Dispose();
                    }
                    catch
                    {
                    }

                    this.activeLog = null;
                }
            }
        }

        private void ShutdownLogging()
        {
            if (this.activeLog == null)
            {
                return;
            }

            try
            {
                this.activeLog.Dispose();
            }
            catch
            {
            }
            finally
            {
                this.activeLog = null;
            }
        }

        private void LogException(Exception exception)
        {
            if (this.activeLog == null || exception == null)
            {
                return;
            }

            try
            {
                this.activeLog.WriteLogOnlyLine("Unhandled exception:");
                this.activeLog.WriteLogOnlyLine(exception.ToString());
            }
            catch
            {
            }
        }

        private void WriteLogOnlyLine(string message)
        {
            if (this.activeLog == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                this.activeLog.WriteLogOnlyLine(message);
            }
            catch
            {
            }
        }
    }
}
