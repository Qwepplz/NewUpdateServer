using System;

namespace UpdateServer.Domain
{
    internal sealed class RepositoryTarget
    {
        public readonly string DisplayName;
        public readonly string GithubOwner;
        public readonly string GithubRepo;
        public readonly string StateKey;
        public readonly string MirrorOwner;
        public readonly string MirrorRepo;

        public RepositoryTarget(string displayName, string githubOwner, string githubRepo, string stateKey, string mirrorOwner, string mirrorRepo)
        {
            DisplayName = displayName;
            GithubOwner = githubOwner;
            GithubRepo = githubRepo;
            StateKey = stateKey;
            MirrorOwner = mirrorOwner;
            MirrorRepo = mirrorRepo;
        }

        public bool HasMirror
        {
            get
            {
                return !string.IsNullOrWhiteSpace(MirrorOwner) && !string.IsNullOrWhiteSpace(MirrorRepo);
            }
        }
    }
}
