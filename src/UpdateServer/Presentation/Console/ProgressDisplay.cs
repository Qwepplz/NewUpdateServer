using System;
using System.IO;

namespace UpdateServer.Presentation
{
    internal sealed class ProgressDisplay : IDisposable
    {
        private readonly TextWriter outputWriter;
        private bool canRefresh;
        private bool initialized;
        private bool completed;
        private bool fallbackHeaderWritten;
        private int firstLineTop;

        public ProgressDisplay(TextWriter writer, bool refresh)
        {
            outputWriter = writer;
            canRefresh = refresh;
        }

        public void Update(string status, string detail)
        {
            if (completed)
            {
                return;
            }

            if (!canRefresh)
            {
                WriteFallbackHeader(status);
                return;
            }

            if (!EnsureInitialized())
            {
                WriteFallbackHeader(status);
                return;
            }

            Render(status, detail);
        }

        public void Complete(string status, string detail)
        {
            if (completed)
            {
                return;
            }

            completed = true;

            if (!canRefresh)
            {
                WriteFallbackHeader(status);
                if (!string.IsNullOrEmpty(detail))
                {
                    outputWriter.WriteLine("       " + detail);
                    outputWriter.Flush();
                }

                return;
            }

            if (!EnsureInitialized())
            {
                WriteFallbackHeader(status);
                if (!string.IsNullOrEmpty(detail))
                {
                    outputWriter.WriteLine("       " + detail);
                    outputWriter.Flush();
                }

                return;
            }

            Render(status, detail);
            MoveCursorBelowStatus();
        }

        public void Dispose()
        {
            if (!completed && initialized && canRefresh)
            {
                MoveCursorBelowStatus();
            }
        }

        private bool EnsureInitialized()
        {
            if (initialized)
            {
                return true;
            }

            try
            {
                outputWriter.WriteLine();
                outputWriter.WriteLine();
                outputWriter.Flush();
                firstLineTop = Math.Max(0, Console.CursorTop - 2);
                initialized = true;
                return true;
            }
            catch
            {
                canRefresh = false;
                return false;
            }
        }

        private void Render(string status, string detail)
        {
            try
            {
                WriteStatusLine(firstLineTop, status);
                WriteStatusLine(firstLineTop + 1, detail);
                Console.SetCursorPosition(0, firstLineTop + 1);
                outputWriter.Flush();
            }
            catch
            {
                canRefresh = false;
            }
        }

        private void WriteStatusLine(int top, string text)
        {
            Console.SetCursorPosition(0, top);
            outputWriter.Write(FitConsoleLine(text));
        }

        private string FitConsoleLine(string text)
        {
            string normalizedText = NormalizeStatusText(text);
            int width = GetConsoleLineWidth();
            if (normalizedText.Length > width)
            {
                if (width <= 3)
                {
                    normalizedText = normalizedText.Substring(0, width);
                }
                else
                {
                    normalizedText = normalizedText.Substring(0, width - 3) + "...";
                }
            }

            return normalizedText.PadRight(width);
        }

        private static string NormalizeStatusText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static int GetConsoleLineWidth()
        {
            try
            {
                return Math.Max(1, Console.BufferWidth - 1);
            }
            catch
            {
                return 79;
            }
        }

        private void MoveCursorBelowStatus()
        {
            try
            {
                Console.SetCursorPosition(0, firstLineTop + 2);
                outputWriter.Flush();
            }
            catch
            {
                canRefresh = false;
            }
        }

        private void WriteFallbackHeader(string status)
        {
            if (fallbackHeaderWritten || string.IsNullOrEmpty(status))
            {
                return;
            }

            outputWriter.WriteLine(status);
            outputWriter.Flush();
            fallbackHeaderWritten = true;
        }
    }
}
