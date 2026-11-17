namespace LocalServerHub.Core.Models;

/// <summary>How the child process's stdio is wired up (plan.md §4.3).</summary>
public enum ServiceIoMode
{
    /// <summary>
    /// Redirected pipes. Default: keeps stdout and stderr separate, which pty
    /// cannot do. Loses ANSI colour and interactive prompts.
    /// </summary>
    Pipe,

    /// <summary>
    /// ConPTY. Full ANSI and interactive stdin, at the cost of merging stdout
    /// and stderr into one stream.
    /// </summary>
    Pty,
}

public enum RestartPolicy
{
    Never,
    OnFailure,
    Always,
}

public enum HealthCheckKind
{
    /// <summary>No probe; the service is considered healthy once the process is alive.</summary>
    None,
    /// <summary>HTTP GET against <see cref="HealthCheck.Url"/>.</summary>
    Http,
    /// <summary>TCP connect to the resolved port.</summary>
    Tcp,
    /// <summary>A regex match on the service's own log output.</summary>
    LogPattern,
}

public sealed record HealthCheck
{
    public HealthCheckKind Kind { get; set; } = HealthCheckKind.None;

    /// <summary>May contain <c>${service.port}</c> and friends.</summary>
    public string? Url { get; init; }

    public IReadOnlyList<int> ExpectStatus { get; set; } = [200];

    /// <summary>Regex applied to log lines when <see cref="Kind"/> is LogPattern.</summary>
    public string? Pattern { get; init; }

    /// <summary>How long the service may take to become healthy before it is Failed.</summary>
    public int TimeoutSec { get; set; } = 60;

    /// <summary>Gap between probe attempts.</summary>
    public int IntervalSec { get; set; } = 2;

    /// <summary>Running HTTP/TCP probe cadence; zero disables ongoing checks.</summary>
    public int MonitorIntervalSec { get; set; } = 30;
}

public sealed record RestartSettings
{
    public RestartPolicy Policy { get; set; } = RestartPolicy.Never;

    public int MaxRetries { get; set; } = 3;

    /// <summary>Backoff in seconds per attempt; the last entry repeats.</summary>
    public IReadOnlyList<int> BackoffSec { get; set; } = [2, 5, 15];

    public int BackoffForAttempt(int attempt)
    {
        if (BackoffSec.Count == 0)
        {
            return 5;
        }

        int index = Math.Clamp(attempt - 1, 0, BackoffSec.Count - 1);
        return BackoffSec[index];
    }
}

/// <summary>A runtime the service needs before it may start (plan.md §3.2).</summary>
public sealed record RuntimeRequirement
{
    /// <summary>Probe id: "node", "python", "conda", "cuda", ...</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Explicit interpreter path, absolute or relative to the service's cwd.
    /// Null means "use whatever is on PATH".
    /// </summary>
    public string? Path { get; init; }

    public string? MinVersion { get; init; }

    /// <summary>When false a missing runtime is reported but does not block Start.</summary>
    public bool IsRequired { get; set; } = true;
}

/// <summary>
/// One registered service and its complete knowledge card. Persisted verbatim
/// to services.json (plan.md §3.4).
/// </summary>
public sealed record ServiceDefinition
{
    /// <summary>Stable identifier; also the log directory name. Never localised.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Working directory. Every relative path in this record resolves against it.</summary>
    public required string Cwd { get; init; }

    /// <summary>
    /// The program to run, absolute or relative to <see cref="Cwd"/>. Batch files
    /// are wrapped by the launcher rather than executed directly (plan.md §4.5).
    /// </summary>
    public required string Executable { get; init; }

    /// <summary>
    /// Fixed arguments that always precede the generated parameter tokens, e.g.
    /// the script path for "node server.js".
    /// </summary>
    public IReadOnlyList<string> BaseArgs { get; set; } = [];

    public string? Description { get; init; }

    /// <summary>Free-form tags. The first one drives the row's category colour.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Port the service is expected to listen on, before parameter overrides.</summary>
    public int? DefaultPort { get; init; }

    public IReadOnlyList<RuntimeRequirement> Runtimes { get; set; } = [];

    /// <summary>Extra environment variables. Values may contain <c>${...}</c> placeholders.</summary>
    public IReadOnlyDictionary<string, string> Env { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ParameterDefinition> Parameters { get; set; } = [];

    public IReadOnlyList<ServicePreset> Presets { get; set; } = [];

    public HealthCheck Health { get; set; } = new();

    public RestartSettings Restart { get; set; } = new();

    public ServiceIoMode Io { get; set; } = ServiceIoMode.Pipe;

    /// <summary>Console encoding of the child process. "auto" sniffs UTF-8 then falls back.</summary>
    public string Encoding { get; set; } = "auto";

    /// <summary>Shown as the "open" affordance once the service is healthy.</summary>
    public string? OpenUrl { get; init; }

    /// <summary>Seconds to wait for a graceful stop before escalating (plan.md §4.1).</summary>
    public int StopTimeoutSec { get; set; } = 10;

    /// <summary>Excluded from "Start all" when false.</summary>
    public bool IsEnabled { get; set; } = true;
}
