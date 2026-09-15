using System;
using System.IO;

namespace LocalserverLib.Common
{
    public static class LocalserverPathHelper
    {
        public static string RootDataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "Localserver");

        public static string LinesFilePath =>
            Path.Combine(RootDataDirectory, "lines.json");

        public static string LegacyServicesFilePath =>
            Path.Combine(RootDataDirectory, "services.json");

        public static string SettingsFilePath =>
            Path.Combine(RootDataDirectory, "settings.json");

        public static string SecretsFilePath =>
            Path.Combine(RootDataDirectory, "secrets.dat");

        public static string StateDirectory =>
            Path.Combine(RootDataDirectory, "State");

        public static string OwnershipFilePath =>
            Path.Combine(StateDirectory, "ownership.json");

        public static string LogsDirectory =>
            Path.Combine(RootDataDirectory, "Logs");

        public static string GetLineLogsDirectory(string lineId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lineId);
            string safeId = string.Join("_", lineId.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(LogsDirectory, safeId);
        }

        public static void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(RootDataDirectory);
            Directory.CreateDirectory(StateDirectory);
        }
    }
}
