// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Kit.Interop;
using Kit.Settings.UI.Library.Utilities;
using ManagedCommon;

namespace Kit.Settings.UI.Helpers
{
    /// <summary>
    /// Records crashes and exception patterns in %LOCALAPPDATA%\Kit\crash.log so failures can be
    /// analyzed after the fact (tools\diagnostics\Get-KitDiagnostics.ps1 summarizes the file).
    /// </summary>
    /// <remarks>
    /// Unhandled exceptions retain types, HRESULTs and stacks. First-chance exceptions are sampled: each
    /// distinct type/HResult/site is written once per session and then counted, because handled
    /// exceptions are routine and writing every one synchronously made the thrower pay a file
    /// append (a single AI Hub settings read used to produce dozens).
    /// </remarks>
    internal static class CrashLog
    {
        private const long MaxFileBytes = 1024 * 1024;
        private const int MaxDistinctFirstChance = 200;

        private static readonly object FileLock = new();
        private static readonly Dictionary<string, int> FirstChanceCounts = new();
        private static readonly object CountsLock = new();
        private static readonly string FilePath = Path.Combine(Constants.AppDataPath(), "crash.log");
        private static int _unsampledCount;

        [ThreadStatic]
        private static bool _inHandler;

        public static void StartSession(string processName)
        {
            try
            {
                lock (FileLock)
                {
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxFileBytes)
                    {
                        File.Move(FilePath, Path.ChangeExtension(FilePath, ".1.log"), overwrite: true);
                    }
                }
            }
            catch (Exception)
            {
                // Rotation is best effort; appending to an oversized file is still useful.
            }

            Append($"[Session {Now}] {processName} started, pid {Environment.ProcessId}, version {Helper.GetProductVersion()}");
            Append("FirstChance entries are sampled exception observations, not proof of a crash. Unhandled entries report failures.");
        }

        public static void RecordUnhandled(string source, Exception ex)
        {
            if (ex == null)
            {
                return;
            }

            var details = new StringBuilder();
            for (int depth = 0; ex != null && depth < 10; depth++, ex = ex.InnerException)
            {
                details.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{ex.GetType().FullName}: HRESULT=0x{ex.HResult:X8}");
                details.AppendLine(ex.StackTrace);
            }

            Append($"[Unhandled {Now}] ({source}) {details}");
            Logger.LogError($"Unhandled exception ({source}): {details}");
        }

        public static void RecordFirstChance(Exception ex)
        {
            if (ex == null || _inHandler || IsRoutine(ex))
            {
                return;
            }

            _inHandler = true;
            try
            {
                string site = new StackTrace(ex, false).GetFrame(0)?.GetMethod() is { } method
                    ? $"{method.DeclaringType?.FullName}.{method.Name}"
                    : "unknown";

                // Exception messages can contain URLs, credentials and private file names.
                string signature = $"HRESULT=0x{ex.HResult:X8}";
                string key = $"{ex.GetType().FullName}|{signature}|{site}";
                bool firstSample = false;
                lock (CountsLock)
                {
                    if (FirstChanceCounts.TryGetValue(key, out int count))
                    {
                        FirstChanceCounts[key] = count + 1;
                    }
                    else if (FirstChanceCounts.Count < MaxDistinctFirstChance)
                    {
                        FirstChanceCounts.Add(key, 1);
                        firstSample = true;
                    }
                    else
                    {
                        _unsampledCount++;
                    }
                }

                if (firstSample)
                {
                    Append($"[FirstChance {Now}] {ex.GetType().FullName}: {signature}\n   at {site}");
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                _inHandler = false;
            }
        }

        /// <summary>Writes how often each sampled first-chance exception recurred this session.</summary>
        public static void EndSession()
        {
            Dictionary<string, int> counts;
            int unsampled;
            lock (CountsLock)
            {
                counts = new Dictionary<string, int>(FirstChanceCounts);
                unsampled = _unsampledCount;
            }

            foreach (var entry in counts)
            {
                if (entry.Value > 1)
                {
                    Append($"[FirstChanceSummary {Now}] x{entry.Value} {entry.Key}");
                }
            }

            if (unsampled > 0)
            {
                Append($"[FirstChanceLimit {Now}] additional observations={unsampled}");
            }

            Append($"[SessionEnd {Now}] pid {Environment.ProcessId}");
        }

        private static bool IsRoutine(Exception ex) =>
            ex is OperationCanceledException ||
            (ex is FileNotFoundException fnf && fnf.FileName != null && fnf.FileName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));

        private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

        private static void Append(string line)
        {
            try
            {
                lock (FileLock)
                {
                    File.AppendAllText(FilePath, line + "\n\n");
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
