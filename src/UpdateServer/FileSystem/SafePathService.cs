using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UpdateServer.FileSystem
{
    internal static class SafePathService
    {
        internal static HashSet<string> BuildProtectedPathSet(string targetDir)
    {
        HashSet<string> protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string executablePath = SyncPathUtility.GetExecutablePath();
        if (!string.IsNullOrEmpty(executablePath))
        {
            protectedPaths.Add(SyncPathUtility.GetFullPath(executablePath));
        }

        string[] knownHelperNames = new[]
        {
            "_UpdateServer.bat",
            "_UpdateServer.ps1",
            "UpdateServer.cs",
            "Build-UpdateServer.bat",
            "Build-UpdateServer.cmd",
            "UpdateServer.exe"
        };

        foreach (string helperName in knownHelperNames)
        {
            string fullPath = SyncPathUtility.GetFullPath(Path.Combine(targetDir, helperName));
            if (File.Exists(fullPath))
            {
                protectedPaths.Add(fullPath);
            }
        }

        string logDirectoryPath = SyncPathUtility.GetLogDirectoryPath(targetDir);
        try
        {
            if (Directory.Exists(logDirectoryPath) && (File.GetAttributes(logDirectoryPath) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (string logFilePath in EnumerateFilesSafely(logDirectoryPath))
                {
                    protectedPaths.Add(SyncPathUtility.GetFullPath(logFilePath));
                }
            }
        }
        catch
        {
        }

        return protectedPaths;
    }

        internal static void WriteFileAtomically(string sourcePath, string destinationPath)
    {
        string parent = Path.GetDirectoryName(destinationPath);
        if (!Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string fileName = Path.GetFileName(destinationPath);
        string stagingPath = Path.Combine(parent, fileName + ".__pug_get5_sync_staging__" + Guid.NewGuid().ToString("N"));
        string backupPath = Path.Combine(parent, fileName + ".__pug_get5_sync_backup__" + Guid.NewGuid().ToString("N"));

        try
        {
            File.Copy(sourcePath, stagingPath, true);
            if (File.Exists(destinationPath))
            {
                File.Replace(stagingPath, destinationPath, backupPath, true);
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            else
            {
                File.Move(stagingPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                try
                {
                    File.Delete(stagingPath);
                }
                catch
                {
                }
            }

            if (File.Exists(backupPath))
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch
                {
                }
            }
        }
    }

        internal static int RemoveStaleUpdaterArtifacts(string targetDir, HashSet<string> protectedPaths)
    {
        List<string> artifactPaths = new List<string>();
        foreach (string path in EnumerateFilesSafely(targetDir))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.IndexOf(".__pug_get5_sync_staging__", StringComparison.OrdinalIgnoreCase) >= 0
                || fileName.IndexOf(".__pug_get5_sync_backup__", StringComparison.OrdinalIgnoreCase) >= 0
                || fileName.IndexOf(".__betterbot_sync_staging__", StringComparison.OrdinalIgnoreCase) >= 0
                || fileName.IndexOf(".__betterbot_sync_backup__", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                artifactPaths.Add(path);
            }
        }

        int removedCount = 0;
        foreach (string path in artifactPaths.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            string fullPath = SyncPathUtility.GetFullPath(path);
            if (protectedPaths.Contains(fullPath))
            {
                continue;
            }

            AssertSafeManagedPath(targetDir, fullPath);
            File.Delete(fullPath);
            RemoveEmptyParentDirectories(fullPath, targetDir);
            removedCount++;
        }

        return removedCount;
    }

        internal static void AssertNoDirectoryConflict(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException("Cannot place file because a directory exists at: " + path);
        }
    }

        internal static IEnumerable<string> EnumerateFilesSafely(string rootDir)
    {
        Stack<string> pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootDir);

        while (pendingDirectories.Count > 0)
        {
            string currentDir = pendingDirectories.Pop();

            IEnumerable<string> files = Enumerable.Empty<string>();
            try
            {
                files = Directory.EnumerateFiles(currentDir);
            }
            catch
            {
            }

            foreach (string file in files)
            {
                yield return file;
            }

            IEnumerable<string> subDirectories = Enumerable.Empty<string>();
            try
            {
                subDirectories = Directory.EnumerateDirectories(currentDir);
            }
            catch
            {
            }

            foreach (string subDirectory in subDirectories)
            {
                try
                {
                    if ((File.GetAttributes(subDirectory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pendingDirectories.Push(subDirectory);
                }
                catch
                {
                }
            }
        }
    }

        internal static void AssertSafeManagedPath(string targetDir, string path)
    {
        string targetRoot = SyncPathUtility.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = SyncPathUtility.GetFullPath(path);

        if (!IsPathWithinTarget(targetRoot, fullPath))
        {
            throw new InvalidOperationException("Refusing to touch a path outside the target folder: " + fullPath);
        }

        string current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            bool exists = Directory.Exists(current) || File.Exists(current);
            if (exists)
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Refusing to touch a reparse point path: " + current);
                }
            }

            string trimmedCurrent = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmedCurrent, targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(trimmedCurrent);
        }
    }

        private static bool IsPathWithinTarget(string targetRoot, string fullPath)
    {
        string normalizedTarget = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedTarget, normalizedFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = normalizedTarget + Path.DirectorySeparatorChar;
        return normalizedFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

        internal static void RemoveEmptyParentDirectories(string filePath, string stopAt)
    {
        string stopFull = SyncPathUtility.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = Path.GetDirectoryName(filePath);

        while (!string.IsNullOrEmpty(current))
        {
            string currentFull = SyncPathUtility.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (currentFull.Length <= stopFull.Length)
            {
                break;
            }

            if (Directory.EnumerateFileSystemEntries(currentFull).Any())
            {
                break;
            }

            Directory.Delete(currentFull, false);
            current = Path.GetDirectoryName(currentFull);
        }
    }
    }
}
