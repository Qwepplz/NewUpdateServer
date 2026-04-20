namespace UpdateServer.Config
{
    internal static class SyncConfiguration
    {
        public const int RequestTimeoutMs = 15000;
        public const string LogDirectoryName = "log";
        public const string LogFilePrefix = "UpdateServer-";
        public const string LogFileDateFormat = "yyyy-MM-dd";
        public const string LogFileExtension = ".log";
        public const string LogArchiveExtension = ".7z";
        public const string LogArchiveTempExtension = ".tmp";
    }
}
