using System.Text.RegularExpressions;
using LocalServerHub.Core.Models;

namespace LocalServerHub.Core.Configuration;

public static class ServiceCatalogValidator
{
    public static void Validate(ServiceCatalog catalog)
    {
        if (catalog.Version != 2)
            throw new NotSupportedException("Unsupported catalog version. This build requires version 2; no automatic downgrade is performed.");
        if (catalog.Services is null) Invalid("services must be an array");
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (ServiceDefinition? service in catalog.Services!)
        {
            if (service is null) Invalid("services cannot contain null");
            ServiceDefinition s = service!;
            if (string.IsNullOrWhiteSpace(s.Id) || s.Id is "." or ".."
                || s.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || s.Id != s.Id.Trim(' ', '.') || !ids.Add(s.Id))
                Invalid("service IDs must be unique, non-empty Windows file names");
            string stem = s.Id.Split('.')[0];
            if (Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                Invalid("service ID is a reserved Windows name");
            if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Cwd) || s.Executable is null)
                Invalid("name, cwd and executable must be strings");
            ValidatePath(s.Cwd!);
            if (!string.IsNullOrEmpty(s.Executable)) ValidatePath(s.Executable);
            if (s.DefaultPort is < 1 or > 65535) Invalid("defaultPort must be between 1 and 65535");
            if (s.StopTimeoutSec is < 1 or > 3600) Invalid("stopTimeoutSec must be between 1 and 3600");
            if (s.BaseArgs is null || s.Tags is null || s.Runtimes is null || s.Env is null
                || s.Parameters is null || s.Presets is null || s.Health is null || s.Restart is null)
                Invalid("service collections and settings cannot be null");
            if (s.BaseArgs!.Any(x => x is null) || s.Tags!.Any(x => x is null)) Invalid("array entries cannot be null");
            if (!Enum.IsDefined(s.Io) || string.IsNullOrWhiteSpace(s.Encoding)) Invalid("invalid io or encoding setting");
            if (s.Encoding!.ToLowerInvariant() is not ("auto" or "utf8" or "utf-8" or "gbk" or "gb2312" or "936"))
                Invalid("encoding must be auto, utf-8 or gbk");
            foreach ((string key, string value) in s.Env!)
                if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0') || value is null || value.Contains('\0'))
                    Invalid("invalid environment entry");
            foreach (RuntimeRequirement? runtime in s.Runtimes!)
            {
                if (runtime is null || string.IsNullOrWhiteSpace(runtime.Kind)) Invalid("runtime kind is required");
                if (!string.IsNullOrEmpty(runtime!.Path)) ValidatePath(runtime.Path);
                if (runtime.MinVersion is not null && !Version.TryParse(runtime.MinVersion, out _)) Invalid("invalid runtime minVersion");
            }
            HealthCheck h = s.Health!;
            if (!Enum.IsDefined(h.Kind) || h.TimeoutSec is < 1 or > 3600 || h.IntervalSec is < 1 or > 3600
                || h.MonitorIntervalSec is < 0 or > 86400 || h.ExpectStatus is null
                || h.ExpectStatus.Any(x => x is < 100 or > 599)) Invalid("invalid health settings");
            if (h.Kind == HealthCheckKind.Http && (string.IsNullOrWhiteSpace(h.Url) || h.ExpectStatus!.Count == 0))
                Invalid("HTTP health requires a URL and expected statuses");
            if (h.Kind == HealthCheckKind.LogPattern)
            {
                if (string.IsNullOrWhiteSpace(h.Pattern)) Invalid("logPattern health requires a pattern");
                try { _ = new Regex(h.Pattern!, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException) { Invalid("invalid health regular expression"); }
            }
            RestartSettings r = s.Restart!;
            if (!Enum.IsDefined(r.Policy) || r.MaxRetries is < 0 or > 100 || r.BackoffSec is null
                || r.BackoffSec.Any(x => x is < 0 or > 86400)) Invalid("invalid restart settings");
            HashSet<string> flags = new(StringComparer.Ordinal);
            foreach (ParameterDefinition? p in s.Parameters!)
            {
                if (p is null || string.IsNullOrWhiteSpace(p.Flag) || string.IsNullOrWhiteSpace(p.Name)
                    || !flags.Add(p.Flag) || !Enum.IsDefined(p.Type) || !Enum.IsDefined(p.Style)
                    || p.Options is null || p.Requires is null || p.ConflictsWith is null
                    || p.Min > p.Max) Invalid("invalid or duplicate parameter definition");
            }
            foreach (ParameterDefinition p in s.Parameters!)
                if (p.Requires.Concat(p.ConflictsWith).Any(x => x is null || !flags.Contains(x))
                    || p.Options.Any(x => x is null)) Invalid("parameter refers to an unknown flag or null option");
            HashSet<string> presets = new(StringComparer.Ordinal);
            foreach (ServicePreset? p in s.Presets!)
                if (p is null || string.IsNullOrWhiteSpace(p.Name) || !presets.Add(p.Name) || p.Args is null
                    || p.Args.Keys.Any(x => !flags.Contains(x))) Invalid("invalid preset or unknown preset flag");
        }
    }

    private static void ValidatePath(string path)
    {
        try
        {
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) Invalid("invalid path");
            _ = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { Invalid("invalid path"); }
    }

    private static void Invalid(string message) => throw new InvalidDataException($"Invalid service catalog: {message}.");
}
