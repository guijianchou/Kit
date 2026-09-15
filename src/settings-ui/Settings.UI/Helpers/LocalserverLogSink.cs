// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LocalServerHub.Core.Logging;
using ManagedCommon;

namespace Kit.Settings.UI.Helpers
{
    /// <summary>
    /// Batches already-redacted service output into the Settings process's existing Kit logger.
    /// </summary>
    internal sealed class LocalserverLogSink : IDisposable
    {
        private const int QueueCapacity = 512;
        private const int MaximumLineCharacters = 8192;
        private const int MaximumServiceIdCharacters = 256;
        private const int MaximumBatchLines = 128;
        private const int MaximumBatchCharacters = 65536;
        private const int BatchDelayMilliseconds = 100;
        private const int ExitDrainMilliseconds = 500;
        private const string TruncationMarker = "... [truncated]";

        private readonly Channel<QueuedLine> _queue;
        private readonly Task _pump;
        private long _droppedLines;
        private int _disposed;

        public LocalserverLogSink()
        {
            _queue = Channel.CreateBounded<QueuedLine>(
                new BoundedChannelOptions(QueueCapacity)
                {
                    SingleReader = true,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                },
                _ => Interlocked.Increment(ref _droppedLines));

            _pump = Task.Run(PumpAsync);
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        public void Enqueue(string serviceId, LogLine line)
        {
            if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(serviceId) || line == null)
            {
                return;
            }

            // Keep bounded copies, never a reference to an oversized source line or service id.
            // ServiceRunner owns redaction; raw streams, commands and configuration do not belong here.
            _queue.Writer.TryWrite(new QueuedLine(
                Truncate(serviceId, MaximumServiceIdCharacters),
                line.Sequence,
                line.TimestampUtc,
                line.Stream,
                Truncate(line.Text ?? string.Empty, MaximumLineCharacters)));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _queue.Writer.TryComplete();
            }

            // A page/window disposal must not wait on the shared logger's synchronous file IO.
            GC.SuppressFinalize(this);
        }

        private async Task PumpAsync()
        {
            var batch = new StringBuilder(MaximumBatchCharacters);
            QueuedLine? pendingLine = null;
            long failedLines = 0;

            try
            {
                while (pendingLine.HasValue || await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        await Task.Delay(BatchDelayMilliseconds).ConfigureAwait(false);
                    }

                    batch.Clear();
                    long droppedLines = Interlocked.Exchange(ref _droppedLines, 0);
                    AppendLossSummary(batch, droppedLines, failedLines);
                    int lineCount = 0;

                    while (lineCount < MaximumBatchLines)
                    {
                        QueuedLine line;
                        if (pendingLine.HasValue)
                        {
                            line = pendingLine.Value;
                            pendingLine = null;
                        }
                        else if (!_queue.Reader.TryRead(out line))
                        {
                            break;
                        }

                        string formatted = FormatLine(line);
                        if (lineCount > 0 && batch.Length + formatted.Length > MaximumBatchCharacters)
                        {
                            pendingLine = line;
                            break;
                        }

                        batch.Append(formatted);
                        lineCount++;
                    }

                    if (TryWrite(batch.ToString()))
                    {
                        failedLines = 0;
                    }
                    else
                    {
                        failedLines += lineCount;
                        Interlocked.Add(ref _droppedLines, droppedLines);
                    }
                }

                // The final batch may have failed, with no later output available to report that loss.
                long remainingDrops = Interlocked.Exchange(ref _droppedLines, 0);
                if (remainingDrops != 0 || failedLines != 0)
                {
                    batch.Clear();
                    AppendLossSummary(batch, remainingDrops, failedLines);
                    TryWrite(batch.ToString());
                }
            }
            finally
            {
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            }
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            Dispose();
            try
            {
                // ProcessExit cannot await; a slow/full/unwritable disk must not hold up shutdown.
                _pump.Wait(ExitDrainMilliseconds);
            }
            catch (Exception)
            {
                // Shutdown is best effort. Do not recurse into the logger to report a sink failure.
            }
        }

        private static bool TryWrite(string text)
        {
            int indentLevel = Trace.IndentLevel;
            try
            {
                Logger.LogInfo(text);
                return true;
            }
            catch (Exception)
            {
                // Trace listeners can fail or be disposed during shutdown. Never fault a service reader.
                return false;
            }
            finally
            {
                // Logger cannot unindent when a listener throws during the payload write.
                Trace.IndentLevel = indentLevel;
            }
        }

        private static void AppendLossSummary(StringBuilder batch, long droppedLines, long failedLines)
        {
            if (droppedLines != 0)
            {
                batch.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "[Localserver] Dropped {0} log line(s) because the Kit log queue was full.",
                    droppedLines).AppendLine();
            }

            if (failedLines != 0)
            {
                batch.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "[Localserver] Kit log writer failed for {0} log line(s); those records may be incomplete.",
                    failedLines).AppendLine();
            }
        }

        private static string FormatLine(QueuedLine line)
        {
            string stream = line.Stream switch
            {
                LogStream.StdOut => "out",
                LogStream.StdErr => "err",
                LogStream.Hub => "hub",
                _ => ((int)line.Stream).ToString(CultureInfo.InvariantCulture),
            };

            string serviceId = line.ServiceId.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            string text = line.Text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            return FormattableString.Invariant($"[Localserver] [service={serviceId}] [stream={stream}] [sequence={line.Sequence}] [time={line.TimestampUtc:O}] {text}{Environment.NewLine}");
        }

        private static string Truncate(string value, int maximumCharacters)
        {
            if (value.Length <= maximumCharacters)
            {
                return value;
            }

            int length = maximumCharacters - TruncationMarker.Length;
            if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            {
                length--;
            }

            return string.Concat(value.AsSpan(0, length), TruncationMarker);
        }

        private readonly record struct QueuedLine(string ServiceId, long Sequence, DateTimeOffset TimestampUtc, LogStream Stream, string Text);
    }
}
