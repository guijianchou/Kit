using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Kit.AIHubLib.Models;

/// <summary>A saved Markdown report and the chart evidence embedded in that same version.</summary>
public sealed record AuditStabilityReportDocument
{
    private const string ChineseMarker = "<!-- kit-report-language:zh -->";
    private const string EnglishMarker = "<!-- kit-report-language:en -->";
    private const string DataMarker = "<!-- kit-report-data:v1 -->";
    private const string DataFence = "```kit-audit-data\n";
    private const int MaximumCharacters = 32 * 1024 * 1024;
    private AuditReportDocumentSnapshot _snapshot = null!;

    public string VersionId { get; private init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; private init; }
    public bool Monthly { get; private init; }
    public AuditPeriodReport Report { get; private init; } = null!;
    public string Markdown { get; private init; } = string.Empty;
    public string SourcePath { get; private init; } = string.Empty;
    public bool RefreshFailed { get; init; }

    public AuditReportHighlights Highlights(bool chinese) => chinese ? _snapshot.ChineseHighlights : _snapshot.EnglishHighlights;
    public IReadOnlyList<AuditReportSection> Sections(bool chinese) => chinese ? _snapshot.ChineseSections : _snapshot.EnglishSections;

    public string Format(bool chinese)
    {
        string startMarker = chinese ? ChineseMarker : EnglishMarker;
        string endMarker = chinese ? EnglishMarker : DataMarker;
        int start = Markdown.IndexOf(startMarker, StringComparison.Ordinal) + startMarker.Length;
        int end = Markdown.IndexOf(endMarker, start, StringComparison.Ordinal);
        return Markdown[start..end].Trim();
    }

    internal static AuditStabilityReportDocument Create(AuditPeriodReport report, bool monthly)
    {
        var snapshot = new AuditReportDocumentSnapshot(1, Guid.NewGuid().ToString("N"), DateTime.UtcNow, monthly, report,
            report.Highlights(true), report.Highlights(false), report.OverviewSections(true), report.OverviewSections(false));
        string title = monthly ? "Windows 系统稳定性月报 / Windows system stability monthly report"
            : "Windows 系统稳定性周报 / Windows system stability weekly report";
        string Body(bool chinese) => (string.Join("\n\n", System.Linq.Enumerable.Select(chinese ? snapshot.ChineseSections : snapshot.EnglishSections,
            section => "## " + section.Title + "\n\n" + section.Text)) + "\n\n" + report.FormatDetails(chinese))
            .Replace("<!-- kit-report-", "&lt;!-- kit-report-", StringComparison.Ordinal);
        string markdown = "# " + title + "\n\n"
            + $"<!-- kit-report-version:{snapshot.VersionId} generated:{snapshot.GeneratedAtUtc:O} -->\n\n"
            + ChineseMarker + "\n\n" + Body(true) + "\n\n" + EnglishMarker + "\n\n" + Body(false) + "\n\n"
            + DataMarker + "\n<details>\n<summary>图表与证据数据 / Chart and evidence data</summary>\n\n"
            + DataFence + JsonSerializer.Serialize(snapshot, AIHubLibJsonContext.Default.AuditReportDocumentSnapshot)
            + "\n```\n</details>\n";
        // Parse the serialized document before publishing it. Display never reads the mutable source report.
        return Parse(markdown);
    }

    public static AuditStabilityReportDocument Read(string path)
    {
        if (new FileInfo(path).Length > MaximumCharacters * 4L) throw new InvalidDataException("The report document is too large.");
        return Parse(File.ReadAllText(path), Path.GetFullPath(path));
    }

    public static AuditStabilityReportDocument Parse(string markdown, string sourcePath = "")
    {
        if (markdown.Length > MaximumCharacters) throw new InvalidDataException("The report document is too large.");
        string normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        int chinese = normalized.IndexOf(ChineseMarker, StringComparison.Ordinal);
        int english = normalized.IndexOf(EnglishMarker, StringComparison.Ordinal);
        int data = normalized.LastIndexOf(DataMarker, StringComparison.Ordinal);
        if (chinese < 0 || english <= chinese || data <= english) throw new InvalidDataException("The report document sections are missing.");
        int start = normalized.IndexOf(DataFence, data, StringComparison.Ordinal);
        int end = start < 0 ? -1 : normalized.IndexOf("\n```", start + DataFence.Length, StringComparison.Ordinal);
        if (start < 0 || end < 0) throw new InvalidDataException("The report chart data is missing.");
        var snapshot = JsonSerializer.Deserialize(normalized[(start + DataFence.Length)..end], AIHubLibJsonContext.Default.AuditReportDocumentSnapshot)
            ?? throw new InvalidDataException("The report chart data is invalid.");
        if (snapshot.SchemaVersion != 1 || !Guid.TryParseExact(snapshot.VersionId, "N", out _)
            || snapshot.GeneratedAtUtc.Kind != DateTimeKind.Utc || snapshot.Report is null
            || snapshot.Report.EndDate < snapshot.Report.StartDate || snapshot.Report.Days is null
            || snapshot.Report.Issues is null || snapshot.Report.TrendDays is null
            || snapshot.ChineseHighlights is null || snapshot.EnglishHighlights is null
            || snapshot.ChineseSections is null || snapshot.EnglishSections is null)
            throw new InvalidDataException("The report document metadata is invalid.");
        return new()
        {
            VersionId = snapshot.VersionId, GeneratedAtUtc = snapshot.GeneratedAtUtc, Monthly = snapshot.Monthly,
            Report = snapshot.Report, Markdown = normalized, SourcePath = sourcePath,
            _snapshot = snapshot,
        };
    }
}

public sealed record AuditReportDocumentSnapshot(int SchemaVersion, string VersionId, DateTime GeneratedAtUtc, bool Monthly, AuditPeriodReport Report,
    AuditReportHighlights ChineseHighlights, AuditReportHighlights EnglishHighlights,
    IReadOnlyList<AuditReportSection> ChineseSections, IReadOnlyList<AuditReportSection> EnglishSections);
