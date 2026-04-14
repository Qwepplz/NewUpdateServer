using System.Collections.Generic;

namespace UpdateServer.Domain
{
    internal sealed class TreeResult
    {
        public string Branch;
        public string Source;
        public List<TreeEntry> Tree;
    }

    internal sealed class JsonResponse<T>
    {
        public string Url;
        public T Value;
    }

    internal sealed class RepoInfo
    {
        public string default_branch { get; set; }
    }

    internal sealed class TreeResponse
    {
        public bool truncated { get; set; }
        public List<TreeEntry> tree { get; set; }
    }

    internal sealed class TreeEntry
    {
        public string path { get; set; }
        public string type { get; set; }
        public string sha { get; set; }
        public long size { get; set; }
    }
}
