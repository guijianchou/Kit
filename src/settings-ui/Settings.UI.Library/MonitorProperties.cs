// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Microsoft.PowerToys.Settings.UI.Library
{
    public class MonitorProperties
    {
        public const string DefaultDownloadsPath = "%USERPROFILE%\\Downloads";
        public const string DefaultCsvFileName = "results.csv";
        public const int DefaultScanIntervalSeconds = 7200;
        public const int DefaultMaxFileSizeMegabytes = 500;
        public const string DefaultHashAlgorithm = "SHA1";
        public const bool DefaultUseIncrementalHashing = true;
        public const bool DefaultRunInBackground = false;
        public const bool DefaultOrganizeDownloads = true;
        public const bool DefaultCleanInstallers = false;

        public MonitorProperties()
        {
            DownloadsPath = new StringProperty(DefaultDownloadsPath);
            CsvFileName = new StringProperty(DefaultCsvFileName);
            ScanIntervalSeconds = new IntProperty(DefaultScanIntervalSeconds);
            MaxFileSizeMegabytes = new IntProperty(DefaultMaxFileSizeMegabytes);
            HashAlgorithm = new StringProperty(DefaultHashAlgorithm);
            UseIncrementalHashing = new BoolProperty(DefaultUseIncrementalHashing);
            RunInBackground = new BoolProperty(DefaultRunInBackground);
            OrganizeDownloads = new BoolProperty(DefaultOrganizeDownloads);
            CleanInstallers = new BoolProperty(DefaultCleanInstallers);
        }

        [JsonPropertyName("downloadsPath")]
        public StringProperty DownloadsPath { get; set; }

        [JsonPropertyName("csvFileName")]
        public StringProperty CsvFileName { get; set; }

        [JsonPropertyName("scanIntervalSeconds")]
        public IntProperty ScanIntervalSeconds { get; set; }

        [JsonPropertyName("maxFileSizeMegabytes")]
        public IntProperty MaxFileSizeMegabytes { get; set; }

        [JsonPropertyName("hashAlgorithm")]
        public StringProperty HashAlgorithm { get; set; }

        [JsonPropertyName("useIncrementalHashing")]
        public BoolProperty UseIncrementalHashing { get; set; }

        [JsonPropertyName("runInBackground")]
        public BoolProperty RunInBackground { get; set; }

        [JsonPropertyName("organizeDownloads")]
        public BoolProperty OrganizeDownloads { get; set; }

        [JsonPropertyName("cleanInstallers")]
        public BoolProperty CleanInstallers { get; set; }
    }
}
