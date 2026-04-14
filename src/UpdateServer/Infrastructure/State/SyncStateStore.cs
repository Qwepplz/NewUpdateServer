using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using UpdateServer.Common;
using UpdateServer.Domain;

namespace UpdateServer.Infrastructure.State
{
    internal static class SyncStateStore
    {
        internal static ImportedState ImportSyncState(string statePath, string legacyManifestPath)
    {
        ImportedState importedState = new ImportedState();

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
                            importedState.TrackedFiles = state.tracked_files.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
                        }

                        if (state.files != null)
                        {
                            foreach (CachedFileState file in state.files)
                            {
                                if (file == null || string.IsNullOrWhiteSpace(file.path))
                                {
                                    continue;
                                }

                                importedState.Files[SyncPathUtility.NormalizeRelativePath(file.path)] = file;
                            }
                        }

                        return importedState;
                    }
                }
            }
            catch
            {
                Console.WriteLine("       Sync cache unreadable. Rebuilding...");
            }
        }

        if (File.Exists(legacyManifestPath))
        {
            importedState.TrackedFiles = SyncPathUtility.ReadManifest(legacyManifestPath);
        }

        return importedState;
    }

        internal static void ExportSyncState(string statePath, List<string> trackedFiles, Dictionary<string, CachedFileState> files)
    {
        List<CachedFileState> fileStates = new List<CachedFileState>();
        foreach (string path in SyncPathUtility.SortKeys(files.Keys))
        {
            CachedFileState fileState = files[path];
            fileState.path = SyncPathUtility.NormalizeRelativePath(path);
            fileStates.Add(fileState);
        }

        SyncState state = new SyncState
        {
            version = 1,
            tracked_files = trackedFiles.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
            files = fileStates
        };

        JavaScriptSerializer serializer = CreateSerializer();
        string json = FormatJsonForReadability(serializer.Serialize(state));
        File.WriteAllText(statePath, json, new UTF8Encoding(false));
    }

        internal static JavaScriptSerializer CreateSerializer()
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
    }
}
