using System.Collections.Generic;

namespace UpdateServer.Remote.Models
{
    internal sealed class TreeResponse
    {
        public bool truncated { get; set; }

        public List<TreeEntry> tree { get; set; }
    }
}
