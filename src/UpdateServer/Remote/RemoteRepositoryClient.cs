using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using UpdateServer.Config;
using UpdateServer.FileSystem;
using UpdateServer.Remote.Models;
using UpdateServer.Security;
using UpdateServer.State;

namespace UpdateServer.Remote
{
    internal static class RemoteRepositoryClient
    {
        internal static TreeResult PrepareRepositoryTree(RepositoryTarget repository, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));

            TreeResult treeResult = GetRemoteTree(repository, remoteKind);
            ProbeRawAccess(repository, treeResult, tempDirectoryPath, remoteKind);
            return treeResult;
        }

        internal static string DownloadVerifiedFileToTemporaryPath(RepositoryTarget repository, string branch, TreeEntry entry, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));
            if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only blob entries can be downloaded.");

            Directory.CreateDirectory(tempDirectoryPath);
            string url = BuildRepositoryRawUrl(repository, branch, entry.path, remoteKind);
            string tempPath = Path.Combine(tempDirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                DownloadToFile(url, tempPath);
                string actualSha = GitBlobHasher.ComputeForFile(tempPath);
                if (!string.Equals(actualSha, entry.sha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Format("Downloaded file SHA mismatch. Expected {0}, got {1}.", entry.sha, actualSha));
                }

                return tempPath;
            }
            catch (Exception exception)
            {
                TryDeleteFile(tempPath);
                throw new InvalidOperationException(url + " => " + exception.Message, exception);
            }
        }

        internal static string GetDefaultBranch(RepositoryTarget repository, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));

            try
            {
                JsonResponse<RepoInfo> response = RequestJsonFromUrl<RepoInfo>(BuildRepositoryInfoUrl(repository, remoteKind));
                if (response.Value != null && !string.IsNullOrWhiteSpace(response.Value.default_branch))
                {
                    return response.Value.default_branch;
                }
            }
            catch
            {
                Console.WriteLine("       Default branch lookup failed, trying common branch names.");
            }

            return "main";
        }

        internal static TreeResult GetRemoteTree(RepositoryTarget repository, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));

            List<string> branchCandidates = new List<string>();
            branchCandidates.Add(GetDefaultBranch(repository, remoteKind));
            branchCandidates.Add("main");
            branchCandidates.Add("master");

            return GetRemoteTree(repository, branchCandidates, remoteKind);
        }

        internal static TreeResult GetRemoteTree(RepositoryTarget repository, IEnumerable<string> branchCandidates, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (branchCandidates == null) throw new ArgumentNullException(nameof(branchCandidates));

            List<string> errors = new List<string>();
            HashSet<string> seenBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string branch in branchCandidates)
            {
                if (string.IsNullOrWhiteSpace(branch) || !seenBranches.Add(branch))
                {
                    continue;
                }

                Console.WriteLine(string.Format("       Reading branch: {0}", branch));

                try
                {
                    string url = BuildRepositoryTreeUrl(repository, branch, remoteKind);
                    JsonResponse<TreeResponse> response = RequestJsonFromUrl<TreeResponse>(url);
                    TreeResponse tree = response.Value;
                    if (tree == null || tree.tree == null)
                    {
                        throw new InvalidOperationException("Repository API returned no file tree.");
                    }

                    if (tree.truncated)
                    {
                        throw new InvalidOperationException("Repository API returned a truncated tree. Refusing to sync because deletion would be unsafe.");
                    }

                    return new TreeResult
                    {
                        Branch = branch,
                        Source = response.Url,
                        Tree = tree.tree
                    };
                }
                catch (Exception exception)
                {
                    errors.Add(branch + " => " + exception.Message);
                }
            }

            throw new InvalidOperationException("Cannot read repository tree." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
        }

        private static string BuildRepositoryInfoUrl(RepositoryTarget repository, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));

            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return string.Format("https://api.github.com/repos/{0}/{1}", repository.GithubOwner, repository.GithubRepo);
            }

            AssertMirrorConfigured(repository);
            return string.Format("https://gitee.com/api/v5/repos/{0}/{1}", repository.MirrorOwner, repository.MirrorRepo);
        }

        private static string BuildRepositoryTreeUrl(RepositoryTarget repository, string branch, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));

            string encodedBranch = Uri.EscapeDataString(branch);
            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return string.Format("https://api.github.com/repos/{0}/{1}/git/trees/{2}?recursive=1", repository.GithubOwner, repository.GithubRepo, encodedBranch);
            }

            AssertMirrorConfigured(repository);
            return string.Format("https://gitee.com/api/v5/repos/{0}/{1}/git/trees/{2}?recursive=1", repository.MirrorOwner, repository.MirrorRepo, encodedBranch);
        }

        private static string BuildRepositoryRawUrl(RepositoryTarget repository, string branch, string relativePath, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Value cannot be empty.", nameof(relativePath));

            string encodedPath = SyncPathUtility.ConvertToUrlPath(relativePath);
            string encodedBranch = Uri.EscapeDataString(branch);
            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return string.Format("https://raw.githubusercontent.com/{0}/{1}/{2}/{3}", repository.GithubOwner, repository.GithubRepo, encodedBranch, encodedPath);
            }

            AssertMirrorConfigured(repository);
            return string.Format("https://gitee.com/{0}/{1}/raw/{2}/{3}", repository.MirrorOwner, repository.MirrorRepo, encodedBranch, encodedPath);
        }

        private static void AssertMirrorConfigured(RepositoryTarget repository)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (!repository.HasMirror)
            {
                throw new InvalidOperationException("Mirror repository is not configured.");
            }
        }

        private static void ProbeRawAccess(RepositoryTarget repository, TreeResult treeResult, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (treeResult == null) throw new ArgumentNullException(nameof(treeResult));

            TreeEntry probeEntry = null;
            foreach (TreeEntry item in treeResult.Tree)
            {
                if (item == null
                    || !string.Equals(item.type, "blob", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(item.path))
                {
                    continue;
                }

                if (probeEntry == null || item.size < probeEntry.size)
                {
                    probeEntry = item;
                }
            }

            if (probeEntry == null)
            {
                return;
            }

            string tempPath = null;
            try
            {
                tempPath = DownloadVerifiedFileToTemporaryPath(repository, treeResult.Branch, probeEntry, tempDirectoryPath, remoteKind);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    TryDeleteFile(tempPath);
                }
            }
        }

        private static JsonResponse<T> RequestJsonFromUrl<T>(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Value cannot be empty.", nameof(url));

            try
            {
                string content = DownloadString(url, "application/json");
                JavaScriptSerializer serializer = SyncStateStore.CreateSerializer();
                T value = serializer.Deserialize<T>(content);
                return new JsonResponse<T> { Url = url, Value = value };
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(url + " => " + exception.Message, exception);
            }
        }

        private static string DownloadString(string url, string accept)
        {
            HttpWebRequest request = CreateRequest(url, accept);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = EnsureStream(response.GetResponseStream()))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void DownloadToFile(string url, string destination)
        {
            HttpWebRequest request = CreateRequest(url, "application/octet-stream, */*");
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = EnsureStream(response.GetResponseStream()))
            using (FileStream fileStream = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(fileStream);
            }
        }

        private static HttpWebRequest CreateRequest(string url, string accept)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "PugGet5Sync";
            request.Accept = accept;
            request.Timeout = SyncConfiguration.RequestTimeoutMs;
            request.ReadWriteTimeout = SyncConfiguration.RequestTimeoutMs;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Proxy = WebRequest.DefaultWebProxy;
            return request;
        }

        private static Stream EnsureStream(Stream stream)
        {
            if (stream == null)
            {
                throw new InvalidOperationException("Remote endpoint returned no response stream.");
            }

            return stream;
        }

        private static void TryDeleteFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
