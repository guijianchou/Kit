namespace Kit.AiHub.Models;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Catalog of supported local AI agent command-line runners.
/// </summary>
public static class AiKernelCatalog
{
    public const string Codex = "codex";
    public const string Pi = "pi";

    public static readonly FrozenSet<string> SupportedKernels = new[] { Codex, Pi }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes input string to a supported kernel identifier, defaulting to Codex.
    /// </summary>
    public static string Normalize(string? kernel)
    {
        if (string.IsNullOrWhiteSpace(kernel))
        {
            return Codex;
        }

        string trimmed = kernel.Trim();
        return SupportedKernels.Contains(trimmed)
            ? (trimmed.Equals(Pi, StringComparison.OrdinalIgnoreCase) ? Pi : Codex)
            : Codex;
    }

    public static bool IsPi(string? kernel) => Normalize(kernel).Equals(Pi, StringComparison.OrdinalIgnoreCase);

    public static string GetDisplayName(string? kernel) => IsPi(kernel) ? "Pi CLI" : "Codex CLI";
}

/// <summary>
/// Status record indicating installation state and discovered path of a kernel.
/// </summary>
public sealed record AiKernelStatus(string Kernel, bool Installed, string Version, string Path);

/// <summary>
/// Result of checking or downloading kernel updates.
/// </summary>
public sealed record AiKernelUpdateResult(
    AiKernelStatus Status,
    string LatestVersion,
    bool Changed,
    bool UsedBundledArchive = false);
