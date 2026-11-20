// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.Settings.UI.Helpers;

using System;
using System.Collections.Generic;
using Kit.AiHub.Contract;

/// <summary>Formats only structured progress; provider text never enters the status surface.</summary>
internal static class AiTaskProgressText
{
    internal static string Summary(AiTaskProgress progress, bool isChinese)
    {
        var parts = new List<string>();
        if (progress.TotalBatches > 0)
        {
            parts.Add(isChinese
                ? $"成功 {progress.CompletedBatches}/{progress.TotalBatches} 批 · 失败 {progress.FailedBatches} · 进行中 {progress.ActiveBatches} · 排队 {progress.QueuedBatches}"
                : $"Succeeded {progress.CompletedBatches}/{progress.TotalBatches} batches · Failed {progress.FailedBatches} · Active {progress.ActiveBatches} · Queued {progress.QueuedBatches}");
        }

        if (progress.BatchIndex is > 0 && progress.BatchIndex <= progress.TotalBatches)
        {
            parts.Add(isChinese ? $"第 {progress.BatchIndex} 批" : $"Batch {progress.BatchIndex}");
        }

        parts.Add(Activity(progress, isChinese));
        parts.Add(Elapsed(progress.ElapsedMilliseconds, isChinese));
        if (progress.QueueElapsedMilliseconds is > 0)
        {
            parts.Add(isChinese
                ? $"排队用时 {Duration(progress.QueueElapsedMilliseconds.Value)}"
                : $"Time queued {Duration(progress.QueueElapsedMilliseconds.Value)}");
        }

        return string.Join(" · ", parts);
    }

    internal static string Compact(AiTaskProgress progress, bool isChinese)
    {
        // Keep the phase first and short in NetMap's existing single-line badge.
        string activity = progress.Activity switch
        {
            "queued" => isChinese ? "排队中" : "Queued",
            "waiting" => isChinese ? "等待响应" : "Waiting",
            "reasoning" => isChinese ? "分析中" : "Analyzing",
            "responding" => isChinese ? "生成结果" : "Responding",
            "retrying" => isChinese ? "重试中" : "Retrying",
            "validating" => isChinese ? "验证结果" : "Validating",
            "failed" => isChinese ? "失败" : "Failed",
            "cancelled" => isChinese ? "已取消" : "Cancelled",
            _ => progress.Stage switch
            {
                "Queue" => isChinese ? "排队中" : "Queued",
                "Execute" or "Route" => isChinese ? "等待响应" : "Waiting",
                "Failover" => isChinese ? "切换备用" : "Fallback",
                "Validate" => isChinese ? "已验证" : "Validated",
                "Failed" => isChinese ? "失败" : "Failed",
                "Cancelled" => isChinese ? "已取消" : "Cancelled",
                _ => isChinese ? "准备中" : "Preparing",
            },
        };
        return $"{activity} · {Duration(progress.ElapsedMilliseconds)}";
    }

    private static string Activity(AiTaskProgress progress, bool isChinese)
    {
        string activity = progress.Activity switch
        {
            "queued" => isChinese ? "等待分析空位" : "Waiting for capacity",
            "waiting" => isChinese ? "等待服务响应" : "Waiting for response",
            "reasoning" => isChinese ? "正在分析" : "Analyzing",
            "responding" => isChinese ? "正在生成结果" : "Generating results",
            "retrying" => isChinese ? "连接重试中" : "Retrying connection",
            "validating" => isChinese ? "正在验证结果" : "Validating results",
            "failed" => isChinese ? "批次失败" : "Batch failed",
            "cancelled" => isChinese ? "已取消" : "Cancelled",
            _ => progress.Stage switch
            {
                "Queue" => isChinese ? "等待分析空位" : "Waiting for capacity",
                "Execute" or "Route" => isChinese ? "等待服务响应" : "Waiting for response",
                "Failover" => isChinese ? "正在切换备用服务" : "Switching to fallback",
                "Validate" => isChinese ? "结果已验证" : "Results validated",
                "Failed" => isChinese ? "批次失败" : "Batch failed",
                "Cancelled" => isChinese ? "已取消" : "Cancelled",
                _ => isChinese ? "正在准备分析" : "Preparing analysis",
            },
        };
        string route = progress.Route switch
        {
            "Main" => isChinese ? "主服务" : "Main service",
            "Fallback" => isChinese ? "备用服务" : "Fallback service",
            _ => string.Empty,
        };
        if (route.Length > 0)
        {
            activity = $"{route} · {activity}";
        }

        if (progress.RetryCount is > 0)
        {
            activity += isChinese ? $"（已重试 {progress.RetryCount} 次）" : $" ({progress.RetryCount} retries)";
        }

        return activity;
    }

    internal static string Elapsed(long milliseconds, bool isChinese) =>
        isChinese ? $"已用 {Duration(milliseconds)}" : $"Elapsed {Duration(milliseconds)}";

    private static string Duration(long milliseconds)
    {
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return elapsed.TotalHours >= 1
            ? $"{(long)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(long)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }
}
