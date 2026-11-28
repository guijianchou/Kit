namespace Kit.AiHub.Security;

using System.IO;

public static class DefaultWeeklyReportPolicy
{
    public const string TaskId = "windows-stability-report";
    public const string Version = "weekly-v1";
    public static string Instructions { get; } = Read();

    private static string Read()
    {
        using var stream = typeof(DefaultWeeklyReportPolicy).Assembly.GetManifestResourceStream("Kit.AiHub.Resources.windows-stability-report.AGENTS.md")
            ?? throw new InvalidDataException("The embedded weekly report policy is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
