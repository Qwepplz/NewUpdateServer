using System;
using System.Collections.Generic;

namespace UpdateServer.Sync
{
    internal sealed class SyncSummary
    {
        public SyncSummary()
        {
            this.SkippedConflictFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public int Added { get; set; }

        public int Updated { get; set; }

        public int Removed { get; set; }

        public int ExcludedRemoved { get; set; }

        public int Unchanged { get; set; }

        public HashSet<string> SkippedConflictFiles { get; private set; }
    }
}
