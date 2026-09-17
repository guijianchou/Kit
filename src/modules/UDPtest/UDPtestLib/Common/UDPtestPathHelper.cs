using System;
using System.IO;

namespace UDPtestLib.Common
{
    public static class UDPtestPathHelper
    {
        public static string RootDataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "UDPtest");

        public static string TargetsFilePath =>
            Path.Combine(RootDataDirectory, "targets.json");

        public static string SettingsFilePath =>
            Path.Combine(RootDataDirectory, "settings.json");

        public static string StateDirectory =>
            Path.Combine(RootDataDirectory, "State");

        public static string LogsDirectory =>
            Path.Combine(RootDataDirectory, "Logs");

        public static void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(RootDataDirectory);
            Directory.CreateDirectory(StateDirectory);
        }
    }
}
