using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UpdateServer.Common;
using UpdateServer.Domain;

namespace UpdateServer.Infrastructure.State
{
    internal static class FileStateService
    {
        internal static string ComputeGitBlobSha1(string path)
    {
        FileInfo fileInfo = new FileInfo(path);
        byte[] prefixBytes = Encoding.ASCII.GetBytes("blob " + fileInfo.Length + "\0");

        using (SHA1 sha1 = SHA1.Create())
        using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            sha1.TransformBlock(prefixBytes, 0, prefixBytes.Length, null, 0);
            byte[] buffer = new byte[81920];
            int bytesRead;

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha1.TransformBlock(buffer, 0, bytesRead, null, 0);
            }

            sha1.TransformFinalBlock(new byte[0], 0, 0);
            return SyncPathUtility.ToHexString(sha1.Hash);
        }
    }

        internal static bool TestLocalMatchesRemoteBlob(string path, TreeEntry entry)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        FileInfo fileInfo = new FileInfo(path);
        if (entry.size > 0 && fileInfo.Length != entry.size)
        {
            return false;
        }

        string localSha = ComputeGitBlobSha1(path);
        return string.Equals(localSha, entry.sha, StringComparison.OrdinalIgnoreCase);
    }

        internal static CachedFileState GetLocalFileState(string path, string remoteSha)
    {
        FileInfo fileInfo = new FileInfo(path);
        return new CachedFileState
        {
            path = SyncPathUtility.NormalizeRelativePath(path),
            remote_sha = remoteSha,
            length = fileInfo.Length,
            last_write_utc_ticks = fileInfo.LastWriteTimeUtc.Ticks
        };
    }

        internal static bool TestCachedRemoteMatch(string relativePath, string path, TreeEntry entry, Dictionary<string, CachedFileState> cachedFiles)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        CachedFileState cached;
        if (!cachedFiles.TryGetValue(relativePath, out cached) || cached == null)
        {
            return false;
        }

        if (!string.Equals(cached.remote_sha, entry.sha, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        FileInfo fileInfo = new FileInfo(path);
        return cached.length == fileInfo.Length && cached.last_write_utc_ticks == fileInfo.LastWriteTimeUtc.Ticks;
    }
    }
}
