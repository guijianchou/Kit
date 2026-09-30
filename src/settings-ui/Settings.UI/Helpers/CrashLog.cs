// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
    /// Unhandled exceptions are written in full. First-chance exceptions are only sampled: each
    /// distinct type/message/site is written once per session and then counted, because handled
    /// exceptions are routine and writing every one synchronously made the thrower pay a file
    /// append (a single AI Hub settings read used to produce dozens).
    /// </remarks>
    internal static class CrashLog
    {
        private const long MaxFileBytes = 1024 * 1024;
        private const int MaxDistinctFirstChance = 200;

        private static readonly object FileLock = new();
        private static readonly ConcurrentDictionary<string, int> FirstChanceCounts = new();
        private static readonly string FilePath = Path.Combine(Constants.AppDataPath(), "crash.log");

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
        }

        public static void RecordUnhandled(string source, Exception ex)
        {
            if (ex == null)
            {
                return;
            }

            Append($"[Unhandled {Now}] ({source}) {ex}");
            Logger.LogError($"Unhandled exception ({source})", ex);
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
                string key = $"{ex.GetType().FullName}|{ex.Message}|{site}";
                int count = FirstChanceCounts.AddOrUpdate(key, 1, (_, current) => current + 1);
                if (count == 1 && FirstChanceCounts.Count <= MaxDistinctFirstChance)
                {
                    Append($"[FirstChance {Now}] {ex.GetType().FullName}: {ex.Message}\n   at {site}");
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
            foreach (var entry in FirstChanceCounts)
            {
                if (entry.Value > 1)
                {
                    Append($"[FirstChanceSummary {Now}] x{entry.Value} {entry.Key}");
                }
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
