// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

namespace Microsoft.PowerToys.Monitor;

internal sealed record MonitorCommandLine(
    bool ScanOnce,
    bool Organize,
    bool CleanInstallers,
    bool DryRun,
    bool UseConfiguredActions,
    bool ShowHelp,
    string? DownloadsPath,
    string? CsvPath,
    string? SettingsPath,
    string? ScanId,
    int? IntervalSeconds,
    int? ParentProcessId)
{
    internal static MonitorCommandLine Parse(IReadOnlyList<string> args)
    {
        bool scanOnce = false;
        bool organize = false;
        bool cleanInstallers = false;
        bool dryRun = false;
        bool useConfiguredActions = false;
        bool showHelp = false;
        string? downloadsPath = null;
        string? csvPath = null;
        string? settingsPath = null;
        string? scanId = null;
        int? intervalSeconds = null;
        int? parentProcessId = null;

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--scan-once":
                    scanOnce = true;
                    break;
                case "--organize":
                    organize = true;
                    break;
                case "--clean-installers":
                    cleanInstallers = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--use-configured-actions":
                    useConfiguredActions = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--downloads-path":
                    downloadsPath = ReadValue(args, ref index, argument);
                    break;
                case "--csv-path":
                    csvPath = ReadValue(args, ref index, argument);
                    break;
                case "--settings-path":
                    settingsPath = ReadValue(args, ref index, argument);
                    break;
                case "--scan-id":
                    scanId = ReadValue(args, ref index, argument);
                    break;
                case "--interval-seconds":
                    intervalSeconds = TryParseInt32(ReadValue(args, ref index, argument), argument);
                    break;
                case "--pid":
                    parentProcessId = TryParseInt32(ReadValue(args, ref index, argument), argument);
                    break;
            }
        }

        return new MonitorCommandLine(scanOnce, organize, cleanInstallers, dryRun, useConfiguredActions, showHelp, downloadsPath, csvPath, settingsPath, scanId, intervalSeconds, parentProcessId);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string argument)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException("Missing value for " + argument + ".", nameof(args));
        }

        index++;
        return args[index];
    }

    private static int TryParseInt32(string value, string argument)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue))
        {
            return parsedValue;
        }

        throw new ArgumentException("Invalid value for " + argument + ".", nameof(value));
    }
}
