using System;
using System.Collections.Generic;

namespace UpdateServer.State
{
    internal sealed class ImportedState
    {
        public ImportedState()
        {
            this.TrackedFiles = new List<string>();
            this.Files = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);
        }

        public List<string> TrackedFiles { get; set; }

        public Dictionary<string, CachedFileState> Files { get; private set; }

        public bool CacheUnreadable { get; set; }
    }
}
