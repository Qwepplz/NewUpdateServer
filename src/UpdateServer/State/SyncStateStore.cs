using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using UpdateServer.Config;
using UpdateServer.FileSystem;
using UpdateServer.Remote.Models;

namespace UpdateServer.State
{
    internal interface ISyncStateStore
    {
        string GetTargetHash(string targetDirectoryPath);

        string GetStateRoot(string targetDirectoryPath, string targetHash);

        string GetRepositoryStateDirectory(string stateRoot, string stateKey);

        string GetStateFilePath(string stateDirectoryPath);

        string GetLegacyManifestPath(string stateDirectoryPath);

        ImportedState Import(string stateDirectoryPath);

        void Export(string stateDirectoryPath, IEnumerable<string> trackedFiles, IDictionary<string, CachedFileState> files);

        CachedFileState CreateLocalFileState(string relativePath, string filePath, string remoteSha);

        bool MatchesCachedRemote(string relativePath, string filePath, TreeEntry entry, IDictionary<string, CachedFileState> cachedFiles);
    }

    internal sealed class SyncStateStore : ISyncStateStore
    {
        private readonly ISafePathService safePathService;

        public SyncStateStore(ISafePathService safePathService)
        {
            if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));
            this.safePathService = safePathService;
        }

        public string GetTargetHash(string targetDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(this.safePathService.GetFullPath(targetDirectoryPath).ToLowerInvariant());
                byte[] hash = sha256.ComputeHash(bytes);
                return ToHexString(hash);
            }
        }

        public string GetStateRoot(string targetDirectoryPath, string targetHash)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (string.IsNullOrWhiteSpace(targetHash)) throw new ArgumentException("Value cannot be empty.", nameof(targetHash));

            List<string> baseCandidates = new List<string>();
            string environmentValue = Environment.GetEnvironmentVariable(SyncConfiguration.StateRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                baseCandidates.Add(environmentValue);
            }

            AppendStateRootCandidate(baseCandidates, Environment.GetEnvironmentVariable("LOCALAPPDATA"));
            AppendStateRootCandidate(baseCandidates, Environment.GetEnvironmentVariable("APPDATA"));
            baseCandidates.Add(Path.Combine(Path.GetTempPath(), SyncConfiguration.PrimaryStateRootDirectoryName));

            Exception lastError = null;
            foreach (string baseCandidate in baseCandidates)
            {
                try
                {
                    Directory.CreateDirectory(baseCandidate);
                    string stateDirectoryPath = Path.Combine(baseCandidate, targetHash);
                    Directory.CreateDirectory(stateDirectoryPath);
                    return stateDirectoryPath;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }
            }

            throw new InvalidOperationException(
                string.Format("Cannot create sync state directory. Last error: {0}", lastError == null ? "Unknown error" : lastError.Message));
        }

        public string GetRepositoryStateDirectory(string stateRoot, string stateKey)
        {
            if (string.IsNullOrWhiteSpace(stateRoot)) throw new ArgumentException("Value cannot be empty.", nameof(stateRoot));
            if (string.IsNullOrWhiteSpace(stateKey)) throw new ArgumentException("Value cannot be empty.", nameof(stateKey));
            return Path.Combine(stateRoot, stateKey);
        }

        public string GetStateFilePath(string stateDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(stateDirectoryPath));
            return Path.Combine(stateDirectoryPath, SyncConfiguration.SyncStateFileName);
        }

        public string GetLegacyManifestPath(string stateDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(stateDirectoryPath));
            return Path.Combine(stateDirectoryPath, SyncConfiguration.LegacyManifestFileName);
        }

        public ImportedState Import(string stateDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(stateDirectoryPath));

            ImportedState importedState = new ImportedState();
            string statePath = this.GetStateFilePath(stateDirectoryPath);
            string legacyManifestPath = this.GetLegacyManifestPath(stateDirectoryPath);

            if (File.Exists(statePath))
            {
                try
                {
                    JavaScriptSerializer serializer = CreateSerializer();
                    string raw = File.ReadAllText(statePath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        SyncState state = serializer.Deserialize<SyncState>(raw);
                        if (state != null)
                        {
                            if (state.tracked_files != null)
                            {
                                importedState.TrackedFiles.AddRange(state.tracked_files.Where(item => !string.IsNullOrWhiteSpace(item)));
                            }

                            if (state.files != null)
                            {
                                foreach (CachedFileState file in state.files)
                                {
                                    if (file == null || string.IsNullOrWhiteSpace(file.path))
                                    {
                                        continue;
                                    }

                                    importedState.Files[this.safePathService.NormalizeRelativePath(file.path)] = file;
                                }
                            }

                            return importedState;
                        }
                    }
                }
                catch
                {
                    importedState.CacheUnreadable = true;
                }
            }

            if (File.Exists(legacyManifestPath))
            {
                importedState.TrackedFiles.AddRange(this.ReadManifest(legacyManifestPath));
            }

            return importedState;
        }

        public void Export(string stateDirectoryPath, IEnumerable<string> trackedFiles, IDictionary<string, CachedFileState> files)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(stateDirectoryPath));
            if (trackedFiles == null) throw new ArgumentNullException(nameof(trackedFiles));
            if (files == null) throw new ArgumentNullException(nameof(files));

            Directory.CreateDirectory(stateDirectoryPath);
            List<CachedFileState> fileStates = new List<CachedFileState>();
            foreach (string path in files.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                CachedFileState fileState = files[path];
                fileState.path = this.safePathService.NormalizeRelativePath(path);
                fileStates.Add(fileState);
            }

            SyncState state = new SyncState
            {
                version = SyncConfiguration.SyncStateVersion,
                tracked_files = trackedFiles.Where(item => !string.IsNullOrWhiteSpace(item)).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
                files = fileStates
            };

            JavaScriptSerializer serializer = CreateSerializer();
            string json = FormatJsonForReadability(serializer.Serialize(state));
            File.WriteAllText(this.GetStateFilePath(stateDirectoryPath), json, new UTF8Encoding(false));
        }

        public CachedFileState CreateLocalFileState(string relativePath, string filePath, string remoteSha)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Value cannot be empty.", nameof(relativePath));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Value cannot be empty.", nameof(filePath));

            FileInfo fileInfo = new FileInfo(filePath);
            return new CachedFileState
            {
                path = this.safePathService.NormalizeRelativePath(relativePath),
                remote_sha = remoteSha,
                length = fileInfo.Length,
                last_write_utc_ticks = fileInfo.LastWriteTimeUtc.Ticks
            };
        }

        public bool MatchesCachedRemote(string relativePath, string filePath, TreeEntry entry, IDictionary<string, CachedFileState> cachedFiles)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Value cannot be empty.", nameof(relativePath));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Value cannot be empty.", nameof(filePath));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (cachedFiles == null) throw new ArgumentNullException(nameof(cachedFiles));
            if (!File.Exists(filePath))
            {
                return false;
            }

            CachedFileState cached;
            if (!cachedFiles.TryGetValue(relativePath, out cached) || cached == null)
            {
                return false;
            }

            if (!string.Equals(cached.remote_sha, entry.sha, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            FileInfo fileInfo = new FileInfo(filePath);
            return cached.length == fileInfo.Length && cached.last_write_utc_ticks == fileInfo.LastWriteTimeUtc.Ticks;
        }

        private static void AppendStateRootCandidate(ICollection<string> candidates, string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                return;
            }

            candidates.Add(Path.Combine(basePath, SyncConfiguration.PrimaryStateRootDirectoryName));
        }

        private List<string> ReadManifest(string manifestPath)
        {
            return File.ReadAllLines(manifestPath)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(this.safePathService.NormalizeRelativePath)
                .ToList();
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 256;
            return serializer;
        }

        private static string FormatJsonForReadability(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            StringBuilder builder = new StringBuilder(json.Length + (json.Length / 4));
            int indentLevel = 0;
            bool inString = false;
            bool escaping = false;
            char previousNonWhitespace = '\0';

            foreach (char character in json)
            {
                if (escaping)
                {
                    builder.Append(character);
                    escaping = false;
                    continue;
                }

                if (inString)
                {
                    builder.Append(character);
                    if (character == '\\')
                    {
                        escaping = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (character)
                {
                    case '"':
                        builder.Append(character);
                        inString = true;
                        break;

                    case '{':
                    case '[':
                        builder.Append(character);
                        builder.AppendLine();
                        indentLevel++;
                        AppendJsonIndentation(builder, indentLevel);
                        break;

                    case '}':
                    case ']':
                        indentLevel = Math.Max(0, indentLevel - 1);
                        if (previousNonWhitespace != '{' && previousNonWhitespace != '[')
                        {
                            builder.AppendLine();
                            AppendJsonIndentation(builder, indentLevel);
                        }

                        builder.Append(character);
                        break;

                    case ',':
                        builder.Append(character);
                        builder.AppendLine();
                        AppendJsonIndentation(builder, indentLevel);
                        break;

                    case ':':
                        builder.Append(": ");
                        break;

                    default:
                        if (!char.IsWhiteSpace(character))
                        {
                            builder.Append(character);
                        }
                        break;
                }

                if (!char.IsWhiteSpace(character))
                {
                    previousNonWhitespace = character;
                }
            }

            return builder.ToString();
        }

        private static void AppendJsonIndentation(StringBuilder builder, int indentLevel)
        {
            builder.Append(' ', indentLevel * 2);
        }

        private static string ToHexString(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
