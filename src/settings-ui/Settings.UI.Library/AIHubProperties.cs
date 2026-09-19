// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public class AIHubProperties
    {
        public AIHubProperties()
        {
            RetentionDays = new IntProperty(30);
            ActiveTabIndex = new IntProperty(0);
            AuditMode = new IntProperty(0);
            ScanIntervalHours = new IntProperty(0);
        }

        [JsonPropertyName("retentionDays")]
        public IntProperty RetentionDays { get; set; }

        [JsonPropertyName("activeTabIndex")]
        public IntProperty ActiveTabIndex { get; set; }

        /// <summary>
        /// Audit scan mode: 0 = extended (standard privileges), 1 = full (elevated,
        /// additionally reads the Security and Windows Firewall channels).
        /// </summary>
        [JsonPropertyName("auditMode")]
        public IntProperty AuditMode { get; set; }

        /// <summary>
        /// Scheduled audit interval in hours. 0 disables scheduling; otherwise the audit
        /// re-runs on this cadence using an incremental window.
        /// </summary>
        [JsonPropertyName("scanIntervalHours")]
        public IntProperty ScanIntervalHours { get; set; }
    }
}
