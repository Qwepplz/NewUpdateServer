using System;
using System.Collections.Generic;
using UpdateServer.Config;

namespace UpdateServer.ConsoleUi
{
    internal sealed class StartupMenu
    {
        public List<RepositoryTarget> ShowStartupPrompt(string targetDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));

            Console.WriteLine("Pug/Get5 updater");
            Console.WriteLine();
            Console.WriteLine("Target folder:");
            Console.WriteLine(targetDirectoryPath);
            Console.WriteLine();
            Console.WriteLine("This will sync upstream changes into the current folder.");
            Console.WriteLine("Choose what to sync:");
            Console.WriteLine("1 - pug  (Qwepplz/pug)");
            Console.WriteLine("2 - get5 (Qwepplz/get5)");
            Console.WriteLine("3 - all");
            Console.WriteLine("Press ESC to exit immediately.");
            Console.WriteLine();

            try
            {
                while (true)
                {
                    ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                    if (keyInfo.Key == ConsoleKey.D1 || keyInfo.Key == ConsoleKey.NumPad1)
                    {
                        return SelectRepositories("pug", new List<RepositoryTarget> { RepositoryCatalog.PugRepository });
                    }

                    if (keyInfo.Key == ConsoleKey.D2 || keyInfo.Key == ConsoleKey.NumPad2)
                    {
                        return SelectRepositories("get5", new List<RepositoryTarget> { RepositoryCatalog.Get5Repository });
                    }

                    if (keyInfo.Key == ConsoleKey.D3 || keyInfo.Key == ConsoleKey.NumPad3)
                    {
                        return SelectRepositories("all", new List<RepositoryTarget>(RepositoryCatalog.AllRepositories));
                    }

                    if (keyInfo.Key == ConsoleKey.Escape)
                    {
                        Console.WriteLine("Exited by user.");
                        return new List<RepositoryTarget>();
                    }
                }
            }
            catch
            {
                return new List<RepositoryTarget>(RepositoryCatalog.AllRepositories);
            }
        }

        public bool ShowMirrorConfirmation(RepositoryTarget repository, string githubFailureMessage)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));

            Console.WriteLine("GitHub is unavailable for this sync.");
            if (!string.IsNullOrWhiteSpace(githubFailureMessage))
            {
                Console.WriteLine("GitHub error:");
                Console.WriteLine(githubFailureMessage);
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("You can continue with the Gitee mirror for {0}.", repository.DisplayName));
            Console.WriteLine("Risk: the mirror may lag behind GitHub.");
            Console.WriteLine("Continuing may sync an older version, miss newer files, or remove files that only exist in newer GitHub versions.");
            Console.WriteLine();
            Console.Write("Type YES to continue with the mirror, or press ENTER to cancel: ");

            try
            {
                string input = Console.ReadLine();
                Console.WriteLine();

                if (string.Equals((input ?? string.Empty).Trim(), "YES", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Mirror sync confirmed.");
                    Console.WriteLine();
                    return true;
                }

                Console.WriteLine("Mirror sync canceled.");
                return false;
            }
            catch
            {
                Console.WriteLine();
                return false;
            }
        }

        public void PauseBeforeExit()
        {
            try
            {
                if (Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected)
                {
                    Console.WriteLine();
                    Console.Write("Press any key to continue . . .");
                    Console.ReadKey(true);
                    Console.WriteLine();
                }
            }
            catch
            {
            }
        }

        private static List<RepositoryTarget> SelectRepositories(string selectionName, List<RepositoryTarget> repositories)
        {
            Console.WriteLine("Selected: " + selectionName);
            Console.WriteLine("Starting sync...");
            Console.WriteLine();
            return repositories;
        }
    }
}
