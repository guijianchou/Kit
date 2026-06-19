// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Text.Json;

using MonitorCore = Microsoft.PowerToys.Monitor;

namespace Microsoft.PowerToys.Settings.UI.Services
{
    public sealed class MonitorProgressSnapshotReader
    {
        public MonitorCore.MonitorScanProgressSnapshot ReadLatest(string progressPath)
        {
            if (!File.Exists(progressPath))
            {
                return null;
            }

            try
            {
                return MonitorCore.MonitorScanProgressFileReporter.Read(progressPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is JsonException)
            {
                return null;
            }
        }

        public MonitorCore.MonitorScanProgressSnapshot ReadForScanId(string progressPath, string scanId)
        {
            MonitorCore.MonitorScanProgressSnapshot snapshot = ReadLatest(progressPath);
            if (snapshot == null || !string.Equals(snapshot.ScanId, scanId, StringComparison.Ordinal))
            {
                return null;
            }

            return snapshot;
        }
    }
}
