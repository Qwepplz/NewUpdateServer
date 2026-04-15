using System;
using System.Collections.Generic;

namespace UpdateServer.Sync
{
    internal sealed class SyncSummary
    {
        public int Added;
        public int Updated;
        public int Removed;
        public int ExcludedRemoved;
        public int Unchanged;
        public HashSet<string> SkippedConflictFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Merge(SyncSummary other)
        {
            if (other == null)
            {
                return;
            }

            Added += other.Added;
            Updated += other.Updated;
            Removed += other.Removed;
            ExcludedRemoved += other.ExcludedRemoved;
            Unchanged += other.Unchanged;

            foreach (string path in other.SkippedConflictFiles)
            {
                SkippedConflictFiles.Add(path);
            }
        }
    }
}
