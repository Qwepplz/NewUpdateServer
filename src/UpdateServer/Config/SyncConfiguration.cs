using System.Collections.Generic;

namespace UpdateServer.Config
{
    internal static class SyncConfiguration
    {
        public const int RequestTimeoutMs = 15000;
        public const string RemoteUserAgent = "PugGet5Sync";
        public const string ApiAcceptHeader = "application/json";
        public const string BinaryAcceptHeader = "application/octet-stream, */*";
        public const string LogDirectoryName = "log";
        public const string LogFilePrefix = "UpdateServer-";
        public const string LogFileDateFormat = "yyyy-MM-dd";
        public const string LogFileExtension = ".log";
        public const string LogArchiveExtension = ".7z";
        public const string LogArchiveTempExtension = ".tmp";
        public const string SyncStateFileName = "sync-state.json";
        public const string LegacyManifestFileName = "tracked-files.txt";
        public const string StagingArtifactMarker = ".__pug_get5_sync_staging__";
        public const string BackupArtifactMarker = ".__pug_get5_sync_backup__";
        public const string LegacyStagingArtifactMarker = ".__betterbot_sync_staging__";
        public const string LegacyBackupArtifactMarker = ".__betterbot_sync_backup__";
        public const string MutexNamePrefix = @"Local\PugGet5Sync_";
        public const int SyncStateVersion = 1;
        public const string PrimaryStateRootDirectoryName = "PugGet5Sync";
        public const string StateRootEnvironmentVariable = "PUG_GET5_SYNC_STATE";
        public const string TempRootDirectoryPrefix = "PugGet5Sync_";

        public static readonly IReadOnlyList<string> ArtifactMarkers = new[]
        {
            StagingArtifactMarker,
            BackupArtifactMarker,
            LegacyStagingArtifactMarker,
            LegacyBackupArtifactMarker
        };

        public static readonly IReadOnlyList<string> ProtectedHelperFileNames = new[]
        {
            "_UpdateServer.bat",
            "_UpdateServer.ps1",
            "UpdateServer.cs",
            "Build-UpdateServer.bat",
            "Build-UpdateServer.cmd",
            "UpdateServer.exe"
        };
    }
}
