using System;
using System.Collections.Generic;

namespace UpdateServer.Config
{
    internal static class RepositoryCatalog
    {
        public static readonly RepositoryTarget Get5Repository = new RepositoryTarget("get5", "get5", "Qwepplz", "get5", "SaUrrr", "get5");
        public static readonly HashSet<string> AlwaysSkippedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "addons/sourcemod/scripting/include/logdebug.inc",
            "addons/sourcemod/scripting/include/restorecvars.inc"
        };
    }
}
