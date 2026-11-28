using System;

namespace Kit.AIHubLib.Models;

public sealed record WindowsStabilityReportSnapshot(int Version, string Id, DateTime GeneratedUtc, bool Monthly, WindowsStabilityReportData Data)
{
    public WindowsStabilityReportData? AlternateData { get; init; }
}
