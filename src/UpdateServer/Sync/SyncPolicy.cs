using System;
using System.IO;
using UpdateServer.Config;
using UpdateServer.FileSystem;

namespace UpdateServer.Sync
{
    internal static class SyncPolicy
    {
        internal static bool IsExcludedRootFile(string relativePath)
    {
        string normalized = SyncPathUtility.NormalizeRelativePath(relativePath);
        if (normalized.IndexOf('/') >= 0)
        {
            return false;
        }

        string fileName = Path.GetFileName(normalized);
        return fileName.StartsWith("README", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("LECENSE", StringComparison.OrdinalIgnoreCase);
    }

        internal static bool IsAlwaysSkippedFile(string relativePath)
    {
        return RepositoryCatalog.AlwaysSkippedFiles.Contains(SyncPathUtility.NormalizeRelativePath(relativePath));
    }
    }
}
