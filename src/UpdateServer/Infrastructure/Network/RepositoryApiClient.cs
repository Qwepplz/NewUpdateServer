using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using UpdateServer.Configuration;
using UpdateServer.Domain;
using UpdateServer.Infrastructure.Safety;
using UpdateServer.Infrastructure.State;

namespace UpdateServer.Infrastructure.Network
{
    internal static class RepositoryApiClient
    {
        private static List<string> BuildRepositoryInfoUrls(RepositoryTarget repository)
    {
        List<string> urls = new List<string>();
        urls.Add(string.Format("https://api.github.com/repos/{0}/{1}", repository.GithubOwner, repository.GithubRepo));
        if (repository.HasMirror)
        {
            urls.Add(string.Format("https://gitee.com/api/v5/repos/{0}/{1}", repository.MirrorOwner, repository.MirrorRepo));
        }

        return urls;
    }

        private static List<string> BuildRepositoryTreeUrls(RepositoryTarget repository, string encodedBranch)
    {
        List<string> urls = new List<string>();
        urls.Add(string.Format("https://api.github.com/repos/{0}/{1}/git/trees/{2}?recursive=1", repository.GithubOwner, repository.GithubRepo, encodedBranch));
        if (repository.HasMirror)
        {
            urls.Add(string.Format("https://gitee.com/api/v5/repos/{0}/{1}/git/trees/{2}?recursive=1", repository.MirrorOwner, repository.MirrorRepo, encodedBranch));
        }

        return urls;
    }

        internal static List<string> BuildRepositoryRawUrls(RepositoryTarget repository, string branch, string encodedPath)
    {
        List<string> urls = new List<string>();
        urls.Add(string.Format("https://raw.githubusercontent.com/{0}/{1}/{2}/{3}", repository.GithubOwner, repository.GithubRepo, branch, encodedPath));
        if (repository.HasMirror)
        {
            urls.Add(string.Format("https://gitee.com/{0}/{1}/raw/{2}/{3}", repository.MirrorOwner, repository.MirrorRepo, branch, encodedPath));
        }

        return urls;
    }

        private static JsonResponse<T> RequestJsonFromUrls<T>(IEnumerable<string> urls)
    {
        List<string> errors = new List<string>();
        foreach (string url in urls)
        {
            try
            {
                string content = DownloadString(url, "application/json");
                JavaScriptSerializer serializer = SyncStateStore.CreateSerializer();
                T value = serializer.Deserialize<T>(content);
                return new JsonResponse<T> { Url = url, Value = value };
            }
            catch (Exception exception)
            {
                errors.Add(url + " => " + exception.Message);
            }
        }

        throw new InvalidOperationException("All repository API requests failed." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
    }

        internal static string GetDefaultBranch(RepositoryTarget repository)
    {
        List<string> urls = BuildRepositoryInfoUrls(repository);

        try
        {
            JsonResponse<RepoInfo> response = RequestJsonFromUrls<RepoInfo>(urls);
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

        internal static TreeResult GetRemoteTree(RepositoryTarget repository, IEnumerable<string> branchCandidates)
    {
        List<string> errors = new List<string>();
        HashSet<string> seenBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string branch in branchCandidates)
        {
            if (string.IsNullOrWhiteSpace(branch) || !seenBranches.Add(branch))
            {
                continue;
            }

            Console.WriteLine(string.Format("       Reading branch: {0}", branch));
            string encodedBranch = Uri.EscapeDataString(branch);
            List<string> urls = BuildRepositoryTreeUrls(repository, encodedBranch);

            try
            {
                JsonResponse<TreeResponse> response = RequestJsonFromUrls<TreeResponse>(urls);
                TreeResponse tree = response.Value;
                if (tree == null)
                {
                    throw new InvalidOperationException("Repository API returned no file tree.");
                }

                if (tree.truncated)
                {
                    throw new InvalidOperationException("Repository API returned a truncated tree. Refusing to sync because deletion would be unsafe.");
                }

                if (tree.tree == null)
                {
                    throw new InvalidOperationException("Repository API returned no file tree.");
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

        private static string DownloadString(string url, string accept)
    {
        HttpWebRequest request = CreateRequest(url, accept);
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (Stream stream = response.GetResponseStream())
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

        private static void DownloadToFile(string url, string destination)
    {
        HttpWebRequest request = CreateRequest(url, "application/octet-stream, */*");
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (Stream stream = response.GetResponseStream())
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

        internal static void DownloadRemoteFile(IEnumerable<string> urls, string destination, string expectedBlobSha, string tempRoot)
    {
        List<string> errors = new List<string>();

        foreach (string url in urls)
        {
            string tempPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                DownloadToFile(url, tempPath);
                string actualSha = FileStateService.ComputeGitBlobSha1(tempPath);
                if (!string.Equals(actualSha, expectedBlobSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Format("Downloaded file SHA mismatch. Expected {0}, got {1}", expectedBlobSha, actualSha));
                }

                ManagedPathService.WriteFileAtomically(tempPath, destination);
                File.Delete(tempPath);
                return;
            }
            catch (Exception exception)
            {
                errors.Add(url + " => " + exception.Message);
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        throw new InvalidOperationException("All download attempts failed for " + destination + "." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
    }
    }
}
