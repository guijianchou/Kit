// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kit.WingetLib;

namespace Kit.WingetService;

internal sealed class WingetStateStore(string dataDirectory)
{
    private const long MaximumStateBytes = 4 * 1024 * 1024;
    internal const int MaximumSavedOutputUtf8Bytes = 4 * 1024;
    private const string TruncatedOutputMarker = "[Earlier output truncated for saved history]\n";
    private const string OmittedOutputMarker = "[Output omitted to keep saved history within its storage limit]";
    private readonly string _path = Path.Combine(dataDirectory, "State", "service-state.json");

    internal WingetPersistentState Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumStateBytes)
            {
                return new();
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var state = JsonSerializer.Deserialize(stream, WingetStateJsonContext.Default.WingetPersistentState) ?? new();

            // Ignore implausible future watermarks and broken snapshots rather than suppress
            // daily checks indefinitely after a corrupt file or a system clock correction.
            DateTimeOffset futureLimit = DateTimeOffset.UtcNow.AddDays(2);
            return state with
            {
                LastSuccessUtc = state.LastSuccessUtc > futureLimit ? null : state.LastSuccessUtc,
                RetryAfterUtc = state.RetryAfterUtc > futureLimit ? null : state.RetryAfterUtc,
                ConsecutiveFailures = Math.Clamp(state.ConsecutiveFailures, 0, 8),
                Packages = state.Packages?.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Source)).Take(2048).ToArray() ?? [],
                IgnoredPackages = state.IgnoredPackages?.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Source))
                    .DistinctBy(p => (p.Source.ToUpperInvariant(), p.Id.ToUpperInvariant())).Take(2048).ToArray() ?? [],
                Queue = SanitizeItems(state.Queue, WingetServicePolicy.MaximumQueueItems),
                History = SanitizeItems(state.History, WingetServicePolicy.MaximumHistoryItems),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new();
        }
    }

    private static WingetQueueItem[] SanitizeItems(IReadOnlyList<WingetQueueItem>? items, int maximum)
        => items?.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id)
            && item.Package is { } package && !string.IsNullOrWhiteSpace(package.Id) && !string.IsNullOrWhiteSpace(package.Source)
            && item.Kind is "install" or "upgrade" or "uninstall")
            .DistinctBy(item => item.Id, StringComparer.Ordinal).Take(maximum).Select(LimitSavedOutput).ToArray() ?? [];

    private static WingetQueueItem LimitSavedOutput(WingetQueueItem item)
        => item.Result is { Output: { } output } result ? item with { Result = result with { Output = BoundSavedOutput(output) } } : item;

    private static string BoundSavedOutput(string output)
    {
        if (Encoding.UTF8.GetByteCount(output) <= MaximumSavedOutputUtf8Bytes)
        {
            return output;
        }

        int remainingBytes = MaximumSavedOutputUtf8Bytes - Encoding.UTF8.GetByteCount(TruncatedOutputMarker);
        int start = output.Length;
        while (start > 0)
        {
            int next = start - 1;
            if (next > 0 && char.IsLowSurrogate(output[next]) && char.IsHighSurrogate(output[next - 1]))
            {
                next--;
            }

            int bytes = Encoding.UTF8.GetByteCount(output.AsSpan(next, start - next));
            if (bytes > remainingBytes)
            {
                break;
            }

            remainingBytes -= bytes;
            start = next;
        }

        return TruncatedOutputMarker + output[start..];
    }

    internal void Save(WingetPersistentState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Live output retains 16K UTF-16 characters. Persist only a 4 KiB UTF-8
        // tail per result so 100 history entries cannot exhaust the state file.
        state = state with
        {
            History = state.History.Select(LimitSavedOutput).ToArray(),
            Queue = state.Queue.Select(LimitSavedOutput).ToArray(),
        };
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(state, WingetStateJsonContext.Default.WingetPersistentState);
        if (content.Length > MaximumStateBytes)
        {
            // Preserve every record, result, and recovery marker. Only expendable
            // output tails may be replaced, starting with the oldest history.
            var history = state.History.ToArray();
            var queue = state.Queue.ToArray();
            foreach (var entry in history.Select((item, index) => (Item: item, Index: index, History: true))
                .OrderBy(entry => entry.Item.CompletedUtc)
                .Concat(queue.Select((item, index) => (Item: item, Index: index, History: false))
                    .Where(entry => entry.Item.State is not ("Running" or "Pending"))
                    .OrderBy(entry => entry.Item.CompletedUtc)))
            {
                if (entry.Item.Result is not { Output: { Length: > 0 } output } result || Encoding.UTF8.GetByteCount(output) <= Encoding.UTF8.GetByteCount(OmittedOutputMarker))
                {
                    continue;
                }

                (entry.History ? history : queue)[entry.Index] = entry.Item with { Result = result with { Output = OmittedOutputMarker } };
                state = state with { History = history, Queue = queue };
                content = JsonSerializer.SerializeToUtf8Bytes(state, WingetStateJsonContext.Default.WingetPersistentState);
                if (content.Length <= MaximumStateBytes)
                {
                    break;
                }
            }
        }

        if (content.Length > MaximumStateBytes)
        {
            throw new IOException("The Winget service state exceeds its storage limit.");
        }

        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WingetPersistentState))]
internal sealed partial class WingetStateJsonContext : JsonSerializerContext
{
}
