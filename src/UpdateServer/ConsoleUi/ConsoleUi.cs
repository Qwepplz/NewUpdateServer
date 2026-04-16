using System;
using System.Collections.Generic;
using System.IO;
using UpdateServer.Config;
using UpdateServer.Logging;
using UpdateServer.Sync;

namespace UpdateServer.ConsoleUi
{
    internal static class ConsoleUi
    {
        internal static string FormatProgressStatus(string stageLabel, int current, int total, string metrics)
        {
            return string.Format("{0} {1}/{2} | {3}", stageLabel, Math.Max(0, current), Math.Max(0, total), metrics);
        }

        internal static string FormatProgressBarLine(int current, int total)
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

        internal static ProgressDisplay CreateProgressDisplay()
        {
            TextWriter outputWriter = LoggingService.ConsoleWriter;
            return new ProgressDisplay(outputWriter, CanRefreshProgressDisplay());
        }

        private static bool CanRefreshProgressDisplay()
        {
            try
            {
                return Environment.UserInteractive && !Console.IsOutputRedirected;
            }
            catch
            {
                return false;
            }
        }

        internal static List<RepositoryTarget> ShowStartupPrompt(string targetDir)
        {
            Console.WriteLine("Pug/Get5 updater");
            Console.WriteLine();
            Console.WriteLine("Target folder:");
            Console.WriteLine(targetDir);
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
                ConsoleKeyInfo keyInfo;
                while (true)
                {
                    keyInfo = Console.ReadKey(true);
                    if (keyInfo.Key == ConsoleKey.D1 || keyInfo.Key == ConsoleKey.NumPad1)
                    {
                        Console.WriteLine("Selected: pug");
                        Console.WriteLine("Starting sync...");
                        Console.WriteLine();
                        return new List<RepositoryTarget> { RepositoryCatalog.PugRepository };
                    }

                    if (keyInfo.Key == ConsoleKey.D2 || keyInfo.Key == ConsoleKey.NumPad2)
                    {
                        Console.WriteLine("Selected: get5");
                        Console.WriteLine("Starting sync...");
                        Console.WriteLine();
                        return new List<RepositoryTarget> { RepositoryCatalog.Get5Repository };
                    }

                    if (keyInfo.Key == ConsoleKey.D3 || keyInfo.Key == ConsoleKey.NumPad3)
                    {
                        Console.WriteLine("Selected: all");
                        Console.WriteLine("Starting sync...");
                        Console.WriteLine();
                        return new List<RepositoryTarget>(RepositoryCatalog.AllRepositories);
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

        internal static bool ShowMirrorConfirmation(RepositoryTarget repository, string githubFailureMessage)
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

        internal static void PauseBeforeExit()
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
    }
}
