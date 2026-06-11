using System;
using System.IO;
using UpdateServer.Config;

namespace UpdateServer.Sync
{
    internal static class SyncPolicy
    {
        internal static bool IsExcludedRootFile(string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.IndexOf('/') >= 0)
            {
                return false;
            }

            string fileName = Path.GetFileName(normalized);
            return fileName.StartsWith("README", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("LECENSE", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith(".gitattributes", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsAlwaysSkippedFile(string relativePath)
        {
            return RepositoryCatalog.AlwaysSkippedFiles.Contains(NormalizeRelativePath(relativePath));
        }

        private static string NormalizeRelativePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }
    }
}
