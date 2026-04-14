using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UpdateServer.Configuration;

namespace UpdateServer.Common
{
    internal static class SyncPathUtility
    {
        internal static string GetLogDirectoryPath(string targetDir)
    {
        return GetFullPath(Path.Combine(targetDir, SyncConfiguration.LogDirectoryName));
    }

        internal static string GetExecutablePath()
    {
        try
        {
            return GetFullPath(System.Reflection.Assembly.GetEntryAssembly().Location);
        }
        catch
        {
            return string.Empty;
        }
    }

        internal static string GetFullPath(string path)
    {
        return Path.GetFullPath(path);
    }

        internal static string NormalizeRelativePath(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/');
    }

        internal static string GetTargetHash(string targetDir)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] bytes = Encoding.UTF8.GetBytes(GetFullPath(targetDir).ToLowerInvariant());
            byte[] hash = sha256.ComputeHash(bytes);
            return ToHexString(hash);
        }
    }

        internal static string GetStateDirectory(string targetDir, string targetHash)
    {
        List<string> baseCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUG_GET5_SYNC_STATE")))
        {
            baseCandidates.Add(Environment.GetEnvironmentVariable("PUG_GET5_SYNC_STATE"));
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOCALAPPDATA")))
        {
            baseCandidates.Add(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "PugGet5Sync"));
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPDATA")))
        {
            baseCandidates.Add(Path.Combine(Environment.GetEnvironmentVariable("APPDATA"), "PugGet5Sync"));
        }

        baseCandidates.Add(Path.Combine(Path.GetTempPath(), "PugGet5Sync"));

        Exception lastError = null;
        foreach (string candidate in baseCandidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                string stateDir = Path.Combine(candidate, targetHash);
                Directory.CreateDirectory(stateDir);
                return stateDir;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
        }

        throw new InvalidOperationException(string.Format("Cannot create sync state directory. Last error: {0}", lastError == null ? "Unknown error" : lastError.Message));
    }

        internal static string GetTargetPathFromRelative(string targetDir, string relativePath)
    {
        string windowsRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(targetDir, windowsRelative);
    }

        internal static string ConvertToUrlPath(string relativePath)
    {
        string[] segments = NormalizeRelativePath(relativePath).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join("/", segments.Select(Uri.EscapeDataString).ToArray());
    }

        internal static List<string> ReadManifest(string manifestPath)
    {
        return File.ReadAllLines(manifestPath)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeRelativePath)
            .ToList();
    }

        internal static List<string> SortKeys(IEnumerable<string> keys)
    {
        return keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

        internal static string ToHexString(byte[] bytes)
    {
        StringBuilder builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2"));
        }

        return builder.ToString();
    }
    }
}
