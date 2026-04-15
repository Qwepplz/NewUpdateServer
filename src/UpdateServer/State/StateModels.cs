using System;
using System.Collections.Generic;

namespace UpdateServer.State
{
    internal sealed class SyncState
    {
        public int version { get; set; }
        public List<string> tracked_files { get; set; }
        public List<CachedFileState> files { get; set; }
    }

    internal sealed class CachedFileState
    {
        public string path { get; set; }
        public string remote_sha { get; set; }
        public long length { get; set; }
        public long last_write_utc_ticks { get; set; }
    }

    internal sealed class ImportedState
    {
        public List<string> TrackedFiles = new List<string>();
        public Dictionary<string, CachedFileState> Files = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);
    }
}
