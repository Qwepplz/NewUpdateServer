using System.Collections.Generic;

namespace UpdateServer.Remote.Models
{
    internal sealed class TreeResult
    {
        public string Branch { get; set; }

        public string Source { get; set; }

        public List<TreeEntry> Tree { get; set; }
    }
}
