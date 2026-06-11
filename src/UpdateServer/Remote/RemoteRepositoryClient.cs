using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using UpdateServer.Config;
using UpdateServer.Remote.Models;
using UpdateServer.Security;

namespace UpdateServer.Remote
{
    internal interface IRemoteRepositoryClient
    {
        TreeResult PrepareRepositoryTree(RepositoryTarget repository, string tempDirectoryPath, RepositoryRemoteKind remoteKind);

        string DownloadVerifiedFileToTemporaryPath(RepositoryTarget repository, string branch, TreeEntry entry, string tempDirectoryPath, RepositoryRemoteKind remoteKind);
    }

    internal sealed class RemoteRepositoryClient : IRemoteRepositoryClient
    {
        private readonly IRepositoryUrlBuilder urlBuilder;
        private readonly IGitBlobHasher gitBlobHasher;

        public RemoteRepositoryClient(IRepositoryUrlBuilder urlBuilder, IGitBlobHasher gitBlobHasher)
        {
            if (urlBuilder == null) throw new ArgumentNullException(nameof(urlBuilder));
            if (gitBlobHasher == null) throw new ArgumentNullException(nameof(gitBlobHasher));

            this.urlBuilder = urlBuilder;
            this.gitBlobHasher = gitBlobHasher;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public TreeResult PrepareRepositoryTree(RepositoryTarget repository, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            ValidatePrepareRepositoryTreeArguments(repository, tempDirectoryPath);

            TreeResult treeResult = this.GetRemoteTree(repository, remoteKind);
            this.ProbeRawAccess(repository, treeResult, tempDirectoryPath, remoteKind);
            return treeResult;
        }

        public string DownloadVerifiedFileToTemporaryPath(RepositoryTarget repository, string branch, TreeEntry entry, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            ValidateDownloadArguments(repository, branch, entry, tempDirectoryPath);

            Directory.CreateDirectory(tempDirectoryPath);
            string url = this.urlBuilder.BuildRepositoryRawUrl(repository, branch, entry.path, remoteKind);
            string tempPath = Path.Combine(tempDirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                this.DownloadToFile(url, tempPath);
                string actualSha = this.gitBlobHasher.ComputeForFile(tempPath);
                if (!string.Equals(actualSha, entry.sha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Format("Downloaded file SHA mismatch. Expected {0}, got {1}.", entry.sha, actualSha));
                }

                return tempPath;
            }
            catch (Exception exception)
            {
                this.TryDeleteFile(tempPath);
                throw new InvalidOperationException(url + " => " + exception.Message, exception);
            }
        }

        private string GetDefaultBranch(RepositoryTarget repository, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));

            try
            {
                RemoteJsonResponse<RepoInfo> response = this.RequestJsonFromUrl<RepoInfo>(this.urlBuilder.BuildRepositoryInfoUrl(repository, remoteKind));
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

        private TreeResult GetRemoteTree(RepositoryTarget repository, RepositoryRemoteKind remoteKind)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));

            List<string> branchCandidates = new List<string>
            {
                this.GetDefaultBranch(repository, remoteKind),
                "main",
                "master"
            };

            return this.GetRemoteTree(repository, branchCandidates, remoteKind);
        }

        private TreeResult GetRemoteTree(RepositoryTarget repository, IEnumerable<string> branchCandidates, RepositoryRemoteKind remoteKind)
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
                    string url = this.urlBuilder.BuildRepositoryTreeUrl(repository, branch, remoteKind);
                    RemoteJsonResponse<TreeResponse> response = this.RequestJsonFromUrl<TreeResponse>(url);
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

            throw new InvalidOperationException(
                "Cannot read repository tree." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
        }

        private void ProbeRawAccess(RepositoryTarget repository, TreeResult treeResult, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
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
                tempPath = this.DownloadVerifiedFileToTemporaryPath(repository, treeResult.Branch, probeEntry, tempDirectoryPath, remoteKind);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    this.TryDeleteFile(tempPath);
                }
            }
        }

        private RemoteJsonResponse<T> RequestJsonFromUrl<T>(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Value cannot be empty.", nameof(url));

            try
            {
                string content = this.DownloadString(url, SyncConfiguration.ApiAcceptHeader);
                JavaScriptSerializer serializer = CreateSerializer();
                T value = serializer.Deserialize<T>(content);
                return new RemoteJsonResponse<T> { Url = url, Value = value };
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(url + " => " + exception.Message, exception);
            }
        }

        private string DownloadString(string url, string accept)
        {
            HttpWebRequest request = this.CreateRequest(url, accept);

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(EnsureStream(stream), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private void DownloadToFile(string url, string destination)
        {
            HttpWebRequest request = this.CreateRequest(url, SyncConfiguration.BinaryAcceptHeader);

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (FileStream fileStream = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                EnsureStream(stream).CopyTo(fileStream);
            }
        }

        private HttpWebRequest CreateRequest(string url, string accept)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = SyncConfiguration.RemoteUserAgent;
            request.Accept = accept;
            request.Timeout = SyncConfiguration.RequestTimeoutMs;
            request.ReadWriteTimeout = SyncConfiguration.RequestTimeoutMs;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Proxy = WebRequest.DefaultWebProxy;
            return request;
        }

        private static void ValidatePrepareRepositoryTreeArguments(RepositoryTarget repository, string tempDirectoryPath)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));
        }

        private static void ValidateDownloadArguments(RepositoryTarget repository, string branch, TreeEntry entry, string tempDirectoryPath)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));
            if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only blob entries can be downloaded.");
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 256;
            return serializer;
        }

        private static Stream EnsureStream(Stream stream)
        {
            if (stream == null)
            {
                throw new InvalidOperationException("Remote endpoint returned no response stream.");
            }

            return stream;
        }

        private void TryDeleteFile(string path)
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
