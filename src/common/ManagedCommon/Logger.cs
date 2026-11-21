// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Kit.Interop;

namespace ManagedCommon
{
    public static class Logger
    {
        private static readonly string Error = "Error";
        private static readonly string Warning = "Warning";
        private static readonly string Info = "Info";
        private static readonly string Debug = "Debug";
        private static readonly string TraceFlag = "Trace";

        private static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "Unknown";

        private static readonly object InitializationLock = new();
        private static bool _initialized;

        private static int _activeLogLevel; // 0: Trace, 1: Debug, 2: Info, 3: Warning, 4: Error, 5: Off
        private static bool _isLoggingEnabled = true;

        /// <summary>
        /// Gets or sets a value indicating whether logging is currently active.
        /// </summary>
        public static bool IsLoggingEnabled
        {
            get => _isLoggingEnabled;
            set => _isLoggingEnabled = value;
        }

        /// <summary>
        /// Gets or sets the active log level name (e.g. "trace", "debug", "info", "warn", "err", "off").
        /// </summary>
        public static string LogLevelName { get; private set; } = "trace";

        /// <summary>
        /// Sets the logging state and verbosity level.
        /// </summary>
        public static void SetLogging(bool enabled, string logLevel = "trace")
        {
            _isLoggingEnabled = enabled;
            LogLevelName = string.IsNullOrWhiteSpace(logLevel) ? "trace" : logLevel.ToLowerInvariant();
            if (!enabled || LogLevelName == "off")
            {
                _isLoggingEnabled = false;
                _activeLogLevel = 5;
                return;
            }

            _activeLogLevel = LogLevelName switch
            {
                "trace" => 0,
                "debug" => 1,
                "info" => 2,
                "warn" or "warning" => 3,
                "err" or "error" or "critical" => 4,
                _ => 0,
            };
        }

        private static bool ShouldLog(string type)
        {
            int level = type switch
            {
                "Trace" => 0,
                "Debug" => 1,
                "Info" => 2,
                "Warning" => 3,
                "Error" => 4,
                _ => 2,
            };

            return level >= _activeLogLevel;
        }

        /// <summary>
        /// Loads log settings from %LOCALAPPDATA%\Kit\log_settings.json if present.
        /// </summary>
        public static void LoadLogSettings()
        {
            try
            {
                string settingsPath = Path.Combine(Constants.AppDataPath(), "log_settings.json");
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("logLevel", out var elem))
                    {
                        string level = elem.GetString() ?? "trace";
                        if (string.Equals(level, "off", StringComparison.OrdinalIgnoreCase))
                        {
                            SetLogging(false, "off");
                        }
                        else
                        {
                            SetLogging(true, level);
                        }

                        return;
                    }
                }
            }
            catch
            {
            }

            SetLogging(true, "trace");
        }

        /// <summary>
        /// Opens the logs directory in Windows Explorer.
        /// </summary>
        public static void OpenLogFolder()
        {
            try
            {
                string targetPath = !string.IsNullOrEmpty(AppLogDirectoryPath) && Directory.Exists(AppLogDirectoryPath)
                    ? AppLogDirectoryPath
                    : Constants.AppDataPath();

                Process.Start(new ProcessStartInfo("explorer.exe", targetPath) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        /// <summary>
        /// Gets the path to the log directory for the current version of the app.
        /// </summary>
        public static string CurrentVersionLogDirectoryPath { get; private set; }

        /// <summary>
        /// Gets the path to the current log file.
        /// </summary>
        public static string CurrentLogFile { get; private set; }

        /// <summary>
        /// Gets the path to the log directory for the app.
        /// </summary>
        public static string AppLogDirectoryPath { get; private set; }

        /// <summary>
        /// Initializes the logger and sets the path for logging.
        /// </summary>
        /// <example>InitializeLogger("\\FancyZones\\Editor\\Logs")</example>
        /// <param name="applicationLogPath">The path to the log files folder.</param>
        /// <param name="isLocalLow">If the process using Logger is a low-privilege process.</param>
        public static void InitializeLogger(string applicationLogPath, bool isLocalLow = false)
        {
            lock (InitializationLock)
            {
                if (_initialized)
                {
                    return;
                }

                InitializeLoggerCore(applicationLogPath, isLocalLow);
            }
        }

        private static void InitializeLoggerCore(string applicationLogPath, bool isLocalLow)
        {
            LoadLogSettings();
            var logFile = "Log_" + DateTime.Now.ToString(@"yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";
            string versionedPath = LogDirectoryPath(applicationLogPath, isLocalLow);

            // Keep logs at the caller's configured module location. Access denied does not
            // imply a low-integrity process and must not scatter logs across user folders.
            try
            {
                Directory.CreateDirectory(versionedPath);
                var logFilePath = Path.Combine(versionedPath, logFile);
                var streamWriter = new StreamWriter(new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
                var listener = new TextWriterTraceListener(streamWriter);

                string basePath = Path.GetDirectoryName(versionedPath);
                AppLogDirectoryPath = basePath;
                CurrentVersionLogDirectoryPath = versionedPath;
                CurrentLogFile = logFilePath;

                Trace.Listeners.Add(listener);
                _initialized = true;
                Trace.AutoFlush = true;

                // Clean up old version log folders
                Task.Run(() => DeleteOldVersionLogFolders(basePath, versionedPath));
            }
            catch (Exception ex)
            {
                _isLoggingEnabled = false;
                Trace.WriteLine($"[Kit] Logger cannot be initialized for '{versionedPath}'; file logging is disabled: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static string LogDirectoryPath(string applicationLogPath, bool isLocalLow = false)
        {
            string basePath;
            if (isLocalLow)
            {
                basePath = Environment.GetEnvironmentVariable("userprofile") + "\\appdata\\LocalLow\\Kit" + applicationLogPath;
            }
            else
            {
                basePath = Constants.AppDataPath() + applicationLogPath;
            }

            string versionedPath = Path.Combine(basePath, Version);
            return versionedPath;
        }

        /// <summary>
        /// Retains the current and most recently used previous version, plus fourteen days of logs.
        /// </summary>
        /// <param name="basePath">The base path to the log files folder.</param>
        /// <param name="currentVersionPath">The path to the current version's log folder.</param>
        private static void DeleteOldVersionLogFolders(string basePath, string currentVersionPath)
        {
            try
            {
                if (!Directory.Exists(basePath))
                {
                    return;
                }

                if ((File.GetAttributes(basePath) & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                var cutoff = DateTime.UtcNow.AddDays(-14);
                var dirs = Directory.GetDirectories(basePath)
                    .Select(d => new DirectoryInfo(d))
                    .Where(d => !string.Equals(d.FullName, currentVersionPath, StringComparison.OrdinalIgnoreCase))
                    .Select(d => new { Directory = d, LastWriteUtc = GetLogFolderLastWriteUtc(d) })
                    .Where(d => d.LastWriteUtc.HasValue)
                    .OrderByDescending(d => d.LastWriteUtc)
                    .ThenByDescending(d => d.Directory.Name, StringComparer.OrdinalIgnoreCase)
                    .Skip(1)
                    .Where(d => d.LastWriteUtc < cutoff)
                    .ToList();

                foreach (var candidate in dirs)
                {
                    var directory = candidate.Directory;
                    try
                    {
                        // Recheck after enumeration. Only ordinary log files are eligible;
                        // never recursively remove unknown content or redirected directories.
                        if (GetLogFolderLastWriteUtc(directory) is not DateTime lastWrite || lastWrite >= cutoff)
                        {
                            continue;
                        }

                        foreach (var file in directory.GetFiles("*.log", SearchOption.TopDirectoryOnly))
                        {
                            file.Delete();
                        }

                        directory.Delete();
                        LogInfo($"Deleted old log directory: {directory.FullName}");
                    }
                    catch (Exception ex)
                    {
                        LogError($"Failed to delete old log directory: {directory.FullName}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Error cleaning up old log folders", ex);
            }
        }

        private static DateTime? GetLogFolderLastWriteUtc(DirectoryInfo directory)
        {
            directory.Refresh();
            string version = directory.Name.StartsWith('v') ? directory.Name[1..] : directory.Name;
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 || !System.Version.TryParse(version, out _))
            {
                return null;
            }

            var entries = directory.GetFileSystemInfos();
            if (entries.Any(entry => (entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !string.Equals(entry.Extension, ".log", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return entries.Select(entry => entry.LastWriteTimeUtc).Append(directory.LastWriteTimeUtc).Max();
        }

        public static void LogError(string message, [System.Runtime.CompilerServices.CallerMemberName] string memberName = "", [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "", [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = 0)
        {
            Log(message, Error, memberName, sourceFilePath, sourceLineNumber);
        }

        public static void LogError(string message, Exception ex, [System.Runtime.CompilerServices.CallerMemberName] string memberName = "", [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "", [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = 0)
        {
            if (ex == null)
            {
                Log(message, Error, memberName, sourceFilePath, sourceLineNumber);
            }
            else
            {
                var exMessage =
                    message + Environment.NewLine +
                    ex.GetType() + " (" + ex.HResult + "): " + ex.Message + Environment.NewLine;

                if (ex.InnerException != null)
                {
                    exMessage +=
                        "Inner exception: " + Environment.NewLine +
                        ex.InnerException.GetType() + " (" + ex.InnerException.HResult + "): " + ex.InnerException.Message + Environment.NewLine;
                }

                exMessage +=
                    "Stack trace: " + Environment.NewLine +
                    ex.StackTrace;

                Log(exMessage, Error, memberName, sourceFilePath, sourceLineNumber);
            }
        }

        public static void LogWarning(string message, [System.Runtime.CompilerServices.CallerMemberName] string memberName = "", [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "", [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = 0)
        {
            Log(message, Warning, memberName, sourceFilePath, sourceLineNumber);
        }

        public static void LogInfo(string message, [System.Runtime.CompilerServices.CallerMemberName] string memberName = "", [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "", [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = 0)
        {
            Log(message, Info, memberName, sourceFilePath, sourceLineNumber);
        }

        public static void LogDebug(string message, [System.Runtime.CompilerServices.CallerMemberName] string memberName = "", [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "", [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = 0)
        {
            Log(message, Debug, memberName, sourceFilePath, sourceLineNumber);
        }

        public static void LogTrace([System.Runtime.CompilerServices.CallerMemberName] string memberName = "", [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "", [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = 0)
        {
            Log(string.Empty, TraceFlag, memberName, sourceFilePath, sourceLineNumber);
        }

        private static void Log(string message, string type, string memberName, string sourceFilePath, int sourceLineNumber)
        {
            if (!_isLoggingEnabled || !ShouldLog(type))
            {
                return;
            }

            Trace.WriteLine("[" + DateTime.Now.TimeOfDay + "] [" + type + "] [t-" + Environment.CurrentManagedThreadId + "] " + GetCallerInfo(memberName, sourceFilePath, sourceLineNumber));
            Trace.Indent();
            if (message != string.Empty)
            {
                Trace.WriteLine(message);
            }

            Trace.Unindent();
        }

        private static string GetCallerInfo(string memberName, string sourceFilePath, int sourceLineNumber)
        {
            string callerFileName = "Unknown";

            try
            {
                string fileName = Path.GetFileName(sourceFilePath);
                if (!string.IsNullOrEmpty(fileName))
                {
                    callerFileName = fileName;
                }
            }
            catch (Exception)
            {
                callerFileName = "Unknown";
#if DEBUG
                throw;
#endif
            }

            return $"{callerFileName}::{memberName}::{sourceLineNumber}";
        }
    }
}
