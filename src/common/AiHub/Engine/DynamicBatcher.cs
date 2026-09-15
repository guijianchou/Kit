namespace Kit.AiHub.Engine;

using System;
using System.Collections.Generic;

/// <summary>
/// Calculates dynamic batch sizes and token constraints for LLM dispatch.
/// </summary>
public static class DynamicBatcher
{
    private const int MinBatchSize = 10;
    private const int NormalBatchSize = 30;
    private const int MaxBatchSize = 50;
    private const int DefaultContextWindowTokens = 256_000;
    private const int ResponseOutputTokenBudget = 8_000;
    private const int ContextSafetyMarginTokens = 8_000;

    /// <summary>
    /// Estimates token count from string length using standard character ratio.
    /// </summary>
    public static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 3.5);

    /// <summary>
    /// Calculates dynamic batch size for a collection of input records.
    /// </summary>
    public static int CalculateBatchSize(int totalItems, string effort)
    {
        if (totalItems <= MinBatchSize)
        {
            return Math.Max(1, totalItems);
        }

        int targetSize = effort.ToLowerInvariant() switch
        {
            "max" or "xhigh" => 15,
            "high" => 20,
            "low" => 40,
            _ => NormalBatchSize
        };

        return Math.Clamp(targetSize, MinBatchSize, MaxBatchSize);
    }

    /// <summary>
    /// Splits an input list into contiguous batch chunks.
    /// </summary>
    public static IEnumerable<List<T>> CreateBatches<T>(IReadOnlyList<T> items, int batchSize)
    {
        if (items.Count == 0)
        {
            yield break;
        }

        int effectiveSize = Math.Max(1, batchSize);
        for (int i = 0; i < items.Count; i += effectiveSize)
        {
            int count = Math.Min(effectiveSize, items.Count - i);
            var batch = new List<T>(count);
            for (int j = 0; j < count; j++)
            {
                batch.Add(items[i + j]);
            }
            yield return batch;
        }
    }

    /// <summary>
    /// Keeps both record count and encoded payload size within the prompt budget.
    /// </summary>
    public static IEnumerable<List<T>> CreateBatches<T>(IReadOnlyList<T> items, int batchSize, Func<T, int> getSize, int maxSize)
    {
        ArgumentNullException.ThrowIfNull(getSize);
        var batch = new List<T>();
        int size = 0;
        foreach (T item in items)
        {
            int itemSize = getSize(item);
            if (itemSize < 0 || itemSize > maxSize)
            {
                throw new ArgumentException("An input record exceeds the batch size budget.", nameof(items));
            }

            if (batch.Count > 0 && (batch.Count >= Math.Max(1, batchSize) || size + itemSize > maxSize))
            {
                yield return batch;
                batch = new List<T>();
                size = 0;
            }

            batch.Add(item);
            size += itemSize;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}
