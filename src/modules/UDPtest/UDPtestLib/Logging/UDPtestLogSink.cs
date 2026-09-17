using System.Collections.Concurrent;
using ManagedCommon;

namespace UDPtestLib.Logging
{
    public sealed record LogEntry(
        DateTimeOffset Timestamp,
        string Level,
        string Message);

    public static class UDPtestLogSink
    {
        private const int MaxLogEntries = 500;
        private static readonly ConcurrentQueue<LogEntry> _logs = new();

        public static event Action<LogEntry>? LogAdded;

        public static IReadOnlyList<LogEntry> RecentLogs => _logs.ToArray();

        public static void Info(string message)
        {
            Log("INFO", message);
            Logger.LogInfo($"[UDPtest] {message}");
        }

        public static void Warn(string message)
        {
            Log("WARN", message);
            Logger.LogWarning($"[UDPtest] {message}");
        }

        public static void Error(string message, Exception? ex = null)
        {
            string fullMessage = ex is not null ? $"{message}: {ex.Message}" : message;
            Log("ERROR", fullMessage);
            if (ex is not null)
            {
                Logger.LogError($"[UDPtest] {message}", ex);
            }
            else
            {
                Logger.LogError($"[UDPtest] {message}");
            }
        }

        public static void Clear()
        {
            _logs.Clear();
        }

        private static void Log(string level, string message)
        {
            LogEntry entry = new(DateTimeOffset.Now, level, message);
            _logs.Enqueue(entry);
            while (_logs.Count > MaxLogEntries && _logs.TryDequeue(out _))
            {
            }

            LogAdded?.Invoke(entry);
        }
    }
}
