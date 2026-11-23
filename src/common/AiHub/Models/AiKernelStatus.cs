namespace Kit.AiHub.Models;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

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
public sealed record AiKernelStatus(string Kernel, bool Installed, string Version, string Path)
{
    /// <summary>
    /// Local adapter capabilities, separate from the persisted installation status.
    /// A version baseline is not proof that an endpoint supports native tool calls.
    /// </summary>
    [JsonIgnore]
    public AiKernelCapabilities Capabilities => AiKernelCapabilities.FromStatus(this);
}

/// <summary>The protocol required by a future native delegation adapter.</summary>
public enum AiKernelNativeProtocol
{
    None,
    CodexAppServer,
    PiExtension,
}

/// <summary>
/// Describes the installed kernel without enabling unimplemented execution modes.
/// Host-managed workers are available for both kernels. Native delegation requires
/// an adapter and endpoint validation in addition to meeting a known version baseline.
/// </summary>
public sealed record AiKernelCapabilities(
    bool SupportsHostManagedWorkers,
    bool SupportsNativeDelegation,
    AiKernelNativeProtocol NativeProtocol,
    string NativeProtocolBaselineVersion,
    bool MeetsNativeProtocolBaseline)
{
    internal static AiKernelCapabilities FromStatus(AiKernelStatus status)
    {
        bool codex = string.Equals(status.Kernel, AiKernelCatalog.Codex, StringComparison.OrdinalIgnoreCase);
        bool pi = string.Equals(status.Kernel, AiKernelCatalog.Pi, StringComparison.OrdinalIgnoreCase);
        string baseline = codex ? "0.169.0" : pi ? "1.5.2" : string.Empty;
        string version = status.Version.Split('+', 2)[0];
        bool meetsBaseline = status.Installed && baseline.Length > 0 &&
            System.Version.TryParse(version, out System.Version? installed) &&
            installed >= System.Version.Parse(baseline);

        return new(status.Installed && (codex || pi), false,
            codex ? AiKernelNativeProtocol.CodexAppServer : pi ? AiKernelNativeProtocol.PiExtension : AiKernelNativeProtocol.None,
            baseline, meetsBaseline);
    }
}

/// <summary>
/// Result of checking or downloading kernel updates.
/// </summary>
public sealed record AiKernelUpdateResult(
    AiKernelStatus Status,
    string LatestVersion,
    bool Changed,
    bool UsedBundledArchive = false);
