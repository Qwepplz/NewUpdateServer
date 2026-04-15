using System;
using System.Collections.Generic;

namespace UpdateServer.Config
{
    internal static class RepositoryCatalog
    {
        public const string GithubOwner = "Qwepplz";
        public const string MirrorOwner = "SaUrrr";

        public static readonly RepositoryTarget PugRepository = new RepositoryTarget("pug", GithubOwner, "pug", "pug", MirrorOwner, "pug");
        public static readonly RepositoryTarget Get5Repository = new RepositoryTarget("get5", GithubOwner, "get5", "get5", MirrorOwner, "get5");
        public static readonly RepositoryTarget[] AllRepositories = new[] { PugRepository, Get5Repository };
        public static readonly HashSet<string> AlwaysSkippedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "addons/sourcemod/scripting/include/logdebug.inc",
            "addons/sourcemod/scripting/include/restorecvars.inc"
        };
    }
}
