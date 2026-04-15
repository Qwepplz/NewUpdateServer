using System;
using System.Collections.Generic;
using System.IO;
using UpdateServer.FileSystem;
using UpdateServer.Remote.Models;
using UpdateServer.Security;

namespace UpdateServer.State
{
    internal static class FileStateService
    {
        internal static bool TestLocalMatchesRemoteBlob(string path, TreeEntry entry)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            return GitBlobHasher.MatchesRemoteBlob(path, entry);
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
