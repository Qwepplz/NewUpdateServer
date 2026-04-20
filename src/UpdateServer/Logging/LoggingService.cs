using System;
using System.IO;
using System.Text;
using UpdateServer.Config;
using UpdateServer.FileSystem;

namespace UpdateServer.Logging
{
    internal static class LoggingService
    {
        private static LogSession activeLog;

        internal static TextWriter ConsoleWriter
        {
            get { return activeLog == null ? Console.Out : activeLog.ConsoleWriter; }
        }

        internal static void WriteLogOnlyLine(string message)
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

        internal static void TryInitialize(string targetDir, string[] args)
    {
        if (activeLog != null)
        {
            return;
        }

        try
        {
            activeLog = LogSession.Create(targetDir);
            activeLog.Attach();
            activeLog.WriteSessionStart(targetDir, args);
            Console.WriteLine(string.Format("Log file: {0}", activeLog.CurrentLogPath));
            LogArchiveService.TryArchivePreviousLogs(targetDir, activeLog.CurrentLogPath, activeLog.WriteLogOnlyLine);
        }
        catch
        {
            if (activeLog != null)
            {
                try
                {
                    activeLog.Dispose();
                }
                catch
                {
                }

                activeLog = null;
            }
        }
    }

        internal static void Shutdown()
    {
        if (activeLog == null)
        {
            return;
        }

        try
        {
            activeLog.Dispose();
        }
        catch
        {
        }
        finally
        {
            activeLog = null;
        }
    }

        internal static void LogException(Exception exception)
    {
        if (activeLog == null || exception == null)
        {
            return;
        }

        try
        {
            activeLog.WriteLogOnlyLine("Unhandled exception:");
            activeLog.WriteLogOnlyLine(exception.ToString());
        }
        catch
        {
        }
    }

        private sealed class LogSession : IDisposable
    {
        private readonly TextWriter originalOut;
        private readonly TextWriter originalError;
        private readonly DailyLogFileWriter dailyWriter;
        private readonly TimestampedFileWriter fileWriter;
        private bool attached;
        private bool disposed;

        private LogSession(TextWriter consoleOut, TextWriter consoleError, DailyLogFileWriter writer)
        {
            originalOut = consoleOut;
            originalError = consoleError;
            dailyWriter = writer;
            fileWriter = new TimestampedFileWriter(writer);
        }

        public string CurrentLogPath
        {
            get { return dailyWriter.CurrentPath; }
        }

        public TextWriter ConsoleWriter
        {
            get { return originalOut; }
        }

        public static LogSession Create(string targetDir)
        {
            DailyLogFileWriter writer = new DailyLogFileWriter(targetDir, SyncPathUtility.GetLogDirectoryPath(targetDir));
            return new LogSession(Console.Out, Console.Error, writer);
        }

        public void Attach()
        {
            if (attached)
            {
                return;
            }

            Console.SetOut(new TeeTextWriter(originalOut, fileWriter));
            Console.SetError(new TeeTextWriter(originalError, fileWriter));
            attached = true;
        }

        public void WriteSessionStart(string targetDir, string[] args)
        {
            WriteLogOnlyLine(string.Empty);
            WriteLogOnlyLine("===== Session started =====");
            WriteLogOnlyLine("Target folder: " + targetDir);
            WriteLogOnlyLine("Arguments: " + FormatArguments(args));
        }

        public void WriteLogOnlyLine(string message)
        {
            fileWriter.WriteLine(message ?? string.Empty);
            fileWriter.Flush();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                WriteLogOnlyLine("===== Session ended =====");
                WriteLogOnlyLine(string.Empty);
            }
            catch
            {
            }

            if (attached)
            {
                try
                {
                    Console.Out.Flush();
                }
                catch
                {
                }

                try
                {
                    Console.Error.Flush();
                }
                catch
                {
                }

                Console.SetOut(originalOut);
                Console.SetError(originalError);
                attached = false;
            }

            fileWriter.Dispose();
            dailyWriter.Dispose();
        }

        private static string FormatArguments(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "(none)";
            }

            return string.Join(" ", args);
        }
    }

        private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter primary;
        private readonly TextWriter secondary;

        public TeeTextWriter(TextWriter primaryWriter, TextWriter secondaryWriter)
        {
            primary = primaryWriter;
            secondary = secondaryWriter;
        }

        public override Encoding Encoding
        {
            get { return primary.Encoding; }
        }

        public override void Write(char value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void Write(string value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void WriteLine()
        {
            primary.WriteLine();
            secondary.WriteLine();
        }

        public override void WriteLine(string value)
        {
            primary.WriteLine(value);
            secondary.WriteLine(value);
        }

        public override void Flush()
        {
            primary.Flush();
            secondary.Flush();
        }
    }

        private sealed class DailyLogFileWriter : TextWriter
    {
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private readonly string targetDir;
        private readonly string logDirectoryPath;
        private StreamWriter currentWriter;
        private DateTime currentDate = DateTime.MinValue;
        private string currentPath = string.Empty;

        public DailyLogFileWriter(string targetDirectory, string directoryPath)
        {
            targetDir = targetDirectory;
            logDirectoryPath = directoryPath;
        }

        public string CurrentPath
        {
            get
            {
                EnsureWriter();
                return currentPath;
            }
        }

        public override Encoding Encoding
        {
            get { return Utf8WithoutBom; }
        }

        public override void Write(char value)
        {
            EnsureWriter();
            currentWriter.Write(value);
        }

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            EnsureWriter();
            currentWriter.Write(value);
        }

        public override void WriteLine()
        {
            EnsureWriter();
            currentWriter.WriteLine();
        }

        public override void WriteLine(string value)
        {
            EnsureWriter();
            currentWriter.WriteLine(value);
        }

        public override void Flush()
        {
            if (currentWriter != null)
            {
                currentWriter.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && currentWriter != null)
            {
                currentWriter.Dispose();
                currentWriter = null;
            }

            base.Dispose(disposing);
        }

        private void EnsureWriter()
        {
            DateTime today = DateTime.Now.Date;
            if (currentWriter != null && today == currentDate)
            {
                return;
            }

            string nextPath = SyncPathUtility.GetFullPath(Path.Combine(logDirectoryPath, SyncConfiguration.LogFilePrefix + today.ToString(SyncConfiguration.LogFileDateFormat) + SyncConfiguration.LogFileExtension));
            SafePathService.AssertSafeManagedPath(targetDir, nextPath);
            Directory.CreateDirectory(logDirectoryPath);
            SafePathService.AssertSafeManagedPath(targetDir, nextPath);

            if (currentWriter != null)
            {
                currentWriter.Flush();
                currentWriter.Dispose();
            }

            currentDate = today;
            currentPath = nextPath;
            currentWriter = new StreamWriter(File.Open(currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Utf8WithoutBom);
            currentWriter.AutoFlush = true;
        }
    }

        private sealed class TimestampedFileWriter : TextWriter
    {
        private readonly TextWriter innerWriter;
        private bool isLineStart = true;

        public TimestampedFileWriter(TextWriter writer)
        {
            innerWriter = writer;
        }

        public override Encoding Encoding
        {
            get { return innerWriter.Encoding; }
        }

        public override void Write(char value)
        {
            if (isLineStart && value != '\r' && value != '\n')
            {
                innerWriter.Write("[");
                innerWriter.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                innerWriter.Write("] ");
                isLineStart = false;
            }

            innerWriter.Write(value);

            if (value == '\n')
            {
                isLineStart = true;
            }
            else if (value != '\r')
            {
                isLineStart = false;
            }
        }

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            foreach (char ch in value)
            {
                Write(ch);
            }
        }

        public override void WriteLine()
        {
            innerWriter.WriteLine();
            isLineStart = true;
        }

        public override void WriteLine(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                Write(value);
            }

            innerWriter.WriteLine();
            isLineStart = true;
        }

        public override void Flush()
        {
            innerWriter.Flush();
        }
    }
    }
}
