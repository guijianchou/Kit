using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LocalServerHub.Core.Commands;
using LocalServerHub.Core.Configuration;
using LocalServerHub.Core.Models;
using LocalServerHub.Windows.Native;

namespace LocalServerHub.Windows;

public enum EnvironmentCheckStatus
{
    Neutral,
    Success,
    Warning,
    Error,
}

/// <summary>
/// A repair the user can actually carry out, attached to the check that failed.
/// </summary>
/// <remarks>
/// plan.md §7.4 and line 129 ask for a <c>fixHint</c> that is executable rather than
/// prose: a <c>winget install ...</c> line for a missing runtime, a port to release
/// for a conflict. <see cref="Command"/> is text to copy or run; <see cref="Port"/>
/// and <see cref="ProcessId"/> are set only for a port conflict, which is the one
/// case the Hub can act on itself.
/// </remarks>
public sealed record EnvironmentCheckFix(
    string Description,
    string? Command = null,
    int? Port = null,
    int? ProcessId = null,
    string? ProcessName = null,
    int? SuggestedPort = null,
    long? ProcessStartTimeUtcFileTime = null);

public sealed record EnvironmentCheckItem(
    string Name,
    EnvironmentCheckStatus Status,
    string Summary,
    EnvironmentCheckFix? Fix = null);

public sealed record EnvironmentCheckSnapshot(
    string ServiceId,
    EnvironmentCheckStatus Status,
    string HealthLabel,
    string Summary,
    string RuntimeSummary,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<EnvironmentCheckItem> Items)
{
    public static EnvironmentCheckSnapshot Cancelled(string serviceId) => new(
        serviceId,
        EnvironmentCheckStatus.Warning,
        "CHECKING",
        "Environment check cancelled",
        "—",
        DateTimeOffset.UtcNow,
        []);
}

/// <summary>
/// Performs the checks shown by the selected service's Environment card.
/// Configuration text is not treated as a successful probe: paths, runtime
/// versions, port ownership and runner state are checked independently.
/// </summary>
public static partial class EnvironmentInspector
{
    private static readonly TimeSpan RuntimeProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<EnvironmentCheckSnapshot> CheckAsync(
        ServiceDefinition definition,
        int? port,
        int? processId,
        IReadOnlyCollection<int>? managedProcessIds,
        ServiceState state,
        string? lastError,
        bool? healthProbePassed,
        CancellationToken cancellationToken = default,
        SecretStore? secretStore = null,
        Func<string, string, object?[], string>? formatMessage = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return await Task.Run(
            () => CheckCoreAsync(
                definition,
                port,
                processId,
                managedProcessIds,
                state,
                lastError,
                healthProbePassed,
                cancellationToken,
                secretStore,
                formatMessage),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<EnvironmentCheckItem>> CheckRuntimesAsync(
        ServiceDefinition definition,
        CancellationToken cancellationToken = default,
        Func<string, string, object?[], string>? formatMessage = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return await Task.Run(
            () => CheckRuntimesCoreAsync(definition, cancellationToken, formatMessage),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<EnvironmentCheckSnapshot> CheckCoreAsync(
        ServiceDefinition definition,
        int? port,
        int? processId,
        IReadOnlyCollection<int>? managedProcessIds,
        ServiceState state,
        string? lastError,
        bool? healthProbePassed,
        CancellationToken cancellationToken,
        SecretStore? secretStore,
        Func<string, string, object?[], string>? formatMessage)
    {

        List<EnvironmentCheckItem> items =
        [
            CheckWorkingDirectory(definition, formatMessage),
            CheckExecutable(definition, formatMessage),
        ];

        items.AddRange(await CheckRuntimesCoreAsync(definition, cancellationToken, formatMessage).ConfigureAwait(false));

        if (port is { } configuredPort)
        {
            items.Add(CheckPort(configuredPort, processId, managedProcessIds, state, formatMessage));
        }

        if (state.IsActive() && definition.Health.Kind is not HealthCheckKind.None)
        {
            items.Add(await CheckHealthAsync(definition, port, state, healthProbePassed, cancellationToken, secretStore, formatMessage).ConfigureAwait(false));
        }

        items.Add(CheckServiceState(state, lastError, formatMessage));

        EnvironmentCheckStatus status = items.Max(item => item.Status);
        int issueCount = items.Count(item => item.Status is EnvironmentCheckStatus.Warning or EnvironmentCheckStatus.Error);
        string summary = issueCount == 0
            ? FormatMessage(formatMessage, "Localserver_EnvironmentChecksPassed", "All environment checks passed")
            : issueCount == 1
                ? FormatMessage(formatMessage, "Localserver_EnvironmentIssueSingular", "{0} environment issue", issueCount)
                : FormatMessage(formatMessage, "Localserver_EnvironmentIssuePlural", "{0} environment issues", issueCount);
        string healthLabel = status == EnvironmentCheckStatus.Error
            ? "ISSUE"
            : status == EnvironmentCheckStatus.Warning
                ? state.IsActive() ? "CHECKING" : "WARN"
                : state == ServiceState.Stopped ? "IDLE" : "READY";

        IReadOnlyList<EnvironmentCheckItem> runtimeItems =
            [.. items.Where(item => item.Name.StartsWith("Runtime:", StringComparison.Ordinal))];
        string runtimeSummary = runtimeItems.Count == 0
            ? "None"
            : string.Join(" · ", runtimeItems.Select(item =>
            {
                string statusLabel = item.Status switch
                {
                    EnvironmentCheckStatus.Success => "OK",
                    EnvironmentCheckStatus.Warning => "WARN",
                    EnvironmentCheckStatus.Error => "ERROR",
                    _ => "—",
                };
                return $"{item.Name["Runtime: ".Length..]} {statusLabel}";
            }));

        return new EnvironmentCheckSnapshot(
            definition.Id,
            status,
            healthLabel,
            summary,
            runtimeSummary,
            DateTimeOffset.UtcNow,
            items);
    }

    private static async Task<IReadOnlyList<EnvironmentCheckItem>> CheckRuntimesCoreAsync(
        ServiceDefinition definition,
        CancellationToken cancellationToken,
        Func<string, string, object?[], string>? formatMessage)
    {
        List<EnvironmentCheckItem> items = [];
        foreach (RuntimeRequirement runtime in definition.Runtimes)
        {
            items.Add(await CheckRuntimeCoreAsync(definition, runtime, cancellationToken, formatMessage).ConfigureAwait(false));
        }

        return items;
    }

    private static EnvironmentCheckItem CheckWorkingDirectory(
        ServiceDefinition definition,
        Func<string, string, object?[], string>? formatMessage)
    {
        bool exists = Directory.Exists(definition.Cwd);
        return new(
            "Working directory",
            exists ? EnvironmentCheckStatus.Success : EnvironmentCheckStatus.Error,
            exists ? definition.Cwd : FormatMessage(formatMessage, "Localserver_EnvironmentDirectoryNotFound", "Directory not found: {0}", definition.Cwd));
    }

    private static EnvironmentCheckItem CheckExecutable(
        ServiceDefinition definition,
        Func<string, string, object?[], string>? formatMessage)
    {
        string path = ResolveExecutable(definition);
        bool exists = File.Exists(path);
        return new(
            "Executable",
            exists ? EnvironmentCheckStatus.Success : EnvironmentCheckStatus.Error,
            exists ? path : FormatMessage(formatMessage, "Localserver_ExecutableNotFound", "Executable not found: {0}", path));
    }

    private static async Task<EnvironmentCheckItem> CheckRuntimeCoreAsync(
        ServiceDefinition definition,
        RuntimeRequirement requirement,
        CancellationToken cancellationToken,
        Func<string, string, object?[], string>? formatMessage)
    {
        string? executable = ResolveRuntimeExecutable(definition, requirement);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return RuntimeResult(requirement, RequirementFailureStatus(requirement),
                FormatMessage(formatMessage, "Localserver_RuntimeProbeUnavailable", "No probe is registered for this runtime kind."));
        }

        // Only a rooted path names a file that must exist. The bare probes - "node",
        // "python", "conda", "nvidia-smi" - are PATH lookups, and File.Exists is
        // always false for those, so checking them here would report every
        // PATH-resolved runtime as missing.
        if (Path.IsPathRooted(executable) && !File.Exists(executable))
        {
            return RuntimeResult(
                requirement,
                RequirementFailureStatus(requirement),
                FormatMessage(formatMessage, "Localserver_RuntimeNotFound", "Runtime not found: {0}", executable),
                DescribeRuntimeFix(requirement, formatMessage));
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RuntimeProbeTimeout);

            ProcessStartInfo startInfo = new()
            {
                FileName = executable,
                WorkingDirectory = definition.Cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("--version");

            Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return RuntimeResult(requirement, RequirementFailureStatus(requirement),
                    FormatMessage(formatMessage, "Localserver_RuntimeStartFailed", "Runtime process did not start."));
            }

            try
            {
                Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string output = (await standardOutput.ConfigureAwait(false)) + " " + await standardError.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    return RuntimeResult(requirement, RequirementFailureStatus(requirement),
                        FormatMessage(formatMessage, "Localserver_RuntimeVersionFailed", "Runtime version probe failed."));
                }

                if (!TryParseVersion(output, out Version? version))
                {
                    return RuntimeResult(requirement, RequirementFailureStatus(requirement),
                        FormatMessage(formatMessage, "Localserver_RuntimeVersionUnknown", "Runtime found, but its version was not understood."));
                }

                Version? minimum = null;
                if (!string.IsNullOrWhiteSpace(requirement.MinVersion)
                    && !Version.TryParse(requirement.MinVersion, out minimum))
                {
                    return RuntimeResult(
                        requirement,
                        RequirementFailureStatus(requirement),
                        FormatMessage(formatMessage, "Localserver_RuntimeMinimumInvalid", "Minimum version is invalid: {0}", requirement.MinVersion));
                }

                if (minimum is not null && version < minimum)
                {
                    return RuntimeResult(
                        requirement,
                        RequirementFailureStatus(requirement),
                        FormatMessage(formatMessage, "Localserver_RuntimeVersionTooOld", "Found {0}; required {1}+.", version, requirement.MinVersion),
                        DescribeRuntimeFix(requirement, formatMessage));
                }

                return RuntimeResult(requirement, EnvironmentCheckStatus.Success, $"{version} ({executable})");
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        using CancellationTokenSource cleanupTimeout = new();
                        cleanupTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                        try
                        {
                            await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }

                process.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RuntimeResult(requirement, RequirementFailureStatus(requirement),
                FormatMessage(formatMessage, "Localserver_RuntimeProbeTimedOut", "Runtime version probe timed out."));
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException
            or ArgumentException)
        {
            // A Win32Exception here is usually "not on PATH", which is exactly the case
            // the install hint answers.
            return RuntimeResult(
                requirement,
                RequirementFailureStatus(requirement),
                FormatMessage(formatMessage, "Localserver_RuntimeProbeFailed", "Runtime probe failed: {0}",
                    string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message),
                DescribeRuntimeFix(requirement, formatMessage));
        }
    }

    private static async Task<EnvironmentCheckItem> CheckHealthAsync(
        ServiceDefinition definition,
        int? port,
        ServiceState state,
        bool? healthProbePassed,
        CancellationToken cancellationToken,
        SecretStore? secretStore,
        Func<string, string, object?[], string>? formatMessage)
    {
        try
        {
            // A null verdict for a log-pattern probe means the probe could not be
            // evaluated at all - the process was adopted from a previous session,
            // so its output was never captured here. "Did not pass" would read as
            // a broken service, which is a different and wrong claim.
            if (definition.Health.Kind == HealthCheckKind.LogPattern && healthProbePassed is null)
            {
                return new EnvironmentCheckItem(
                    "Health probe",
                    EnvironmentCheckStatus.Warning,
                    FormatMessage(formatMessage, "Localserver_HealthProbeUnverified",
                        "Log-pattern probe cannot be evaluated for a process this Hub adopted; health is unverified rather than failing."));
            }

            bool healthy = definition.Health.Kind switch
            {
                HealthCheckKind.Tcp or HealthCheckKind.Http => await HealthProbe.CheckAsync(
                    definition, port, secretStore, HealthClient, cancellationToken).ConfigureAwait(false),
                HealthCheckKind.LogPattern => healthProbePassed == true,
                _ => false,
            };

            if (healthy)
            {
                return new EnvironmentCheckItem("Health probe", EnvironmentCheckStatus.Success,
                    FormatMessage(formatMessage, "Localserver_HealthProbePassed", "Configured health probe passed."));
            }

            return new EnvironmentCheckItem(
                "Health probe",
                state == ServiceState.Running ? EnvironmentCheckStatus.Error : EnvironmentCheckStatus.Warning,
                FormatMessage(formatMessage, "Localserver_HealthProbeDidNotPass", "Configured health probe did not pass."));
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new EnvironmentCheckItem(
                "Health probe",
                state == ServiceState.Running ? EnvironmentCheckStatus.Error : EnvironmentCheckStatus.Warning,
                FormatMessage(formatMessage, "Localserver_HealthProbeTimedOut", "Configured health probe timed out."));
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or InvalidOperationException)
        {
            return new EnvironmentCheckItem(
                "Health probe",
                state == ServiceState.Running ? EnvironmentCheckStatus.Error : EnvironmentCheckStatus.Warning,
                FormatMessage(formatMessage, "Localserver_HealthProbeFailed", "Health probe failed ({0}).", ex.GetType().Name));
        }
    }

    private static EnvironmentCheckItem CheckPort(
        int port,
        int? processId,
        IReadOnlyCollection<int>? managedProcessIds,
        ServiceState state,
        Func<string, string, object?[], string>? formatMessage)
    {
        PortOwner? owner = PortInspector.FindOwner(port);
        if (owner is null)
        {
            return new(
                "Port",
                state.IsActive() ? EnvironmentCheckStatus.Warning : EnvironmentCheckStatus.Success,
                state.IsActive()
                    ? FormatMessage(formatMessage, "Localserver_PortNotListening", "Port {0} is not listening.", port)
                    : FormatMessage(formatMessage, "Localserver_PortAvailable", "Port {0} is available.", port));
        }

        bool ownedByService = processId is { } rootPid && owner.ProcessId == rootPid
            || managedProcessIds?.Contains(owner.ProcessId) == true
            || (managedProcessIds is null
                && processId is { } fallbackRootPid
                && ProcessResourceInspector.GetProcessTreeIds(fallbackRootPid).Contains(owner.ProcessId));
        if (ownedByService)
        {
            return new("Port", EnvironmentCheckStatus.Success,
                FormatMessage(formatMessage, "Localserver_PortListening", "Port {0} is listening (PID {1}).", port, owner.ProcessId));
        }

        // The conflict is the one failure the Hub can act on itself, so it carries the
        // owner and a port to move to rather than only naming the problem
        // (plan.md §212, and the three-part error text of §773).
        int? suggested = PortInspector.FindNextFreePort(port);
        string advice = suggested is { } free
            ? FormatMessage(formatMessage, "Localserver_ReleasePortSuggestion", "Release PID {0} or switch this target to port {1}.", owner.ProcessId, free)
            : FormatMessage(formatMessage, "Localserver_ReleasePortManual", "Release PID {0}, or pick a port by hand - nothing near {1} is free.", owner.ProcessId, port);

        return new(
            "Port",
            state.IsActive() ? EnvironmentCheckStatus.Error : EnvironmentCheckStatus.Warning,
            FormatMessage(formatMessage, "Localserver_PortOccupied", "Port {0} is occupied by PID {1} ({2}).", port, owner.ProcessId, owner.ProcessName),
            new EnvironmentCheckFix(
                advice,
                Port: port,
                ProcessId: owner.ProcessId,
                ProcessName: owner.ProcessName,
                SuggestedPort: suggested,
                ProcessStartTimeUtcFileTime: owner.StartTimeUtcFileTime));
    }

    private static EnvironmentCheckItem CheckServiceState(
        ServiceState state,
        string? lastError,
        Func<string, string, object?[], string>? formatMessage) => state switch
    {
        ServiceState.Running => new("Service health", EnvironmentCheckStatus.Success,
            FormatMessage(formatMessage, "Localserver_ServiceReportedRunning", "Runner reports Running.")),
        ServiceState.Stopped => new("Service health", EnvironmentCheckStatus.Neutral,
            FormatMessage(formatMessage, "Localserver_ServiceStopped", "Service is stopped.")),
        ServiceState.Crashed or ServiceState.Failed or ServiceState.StopFailed =>
            new("Service health", EnvironmentCheckStatus.Error, string.IsNullOrWhiteSpace(lastError)
                ? FormatMessage(formatMessage, $"Localserver_State{state}", state.ToDisplayLabel())
                : lastError),
        _ => new("Service health", EnvironmentCheckStatus.Warning,
            FormatMessage(formatMessage, $"Localserver_State{state}", state.ToDisplayLabel())),
    };

    private static EnvironmentCheckStatus RequirementFailureStatus(RuntimeRequirement requirement) =>
        requirement.IsRequired ? EnvironmentCheckStatus.Error : EnvironmentCheckStatus.Warning;

    private static EnvironmentCheckItem RuntimeResult(
        RuntimeRequirement requirement,
        EnvironmentCheckStatus status,
        string summary,
        EnvironmentCheckFix? fix = null) =>
        new($"Runtime: {requirement.Kind}", status, summary, fix);

    /// <summary>
    /// The repair for a missing runtime, as a command that can be run rather than a
    /// suggestion to go and find an installer.
    /// </summary>
    /// <remarks>
    /// winget is the first-class repair path on this machine (plan.md line 129: winget
    /// 1.29.290 confirmed working), so the hint is the exact install line. A venv is
    /// the exception - nothing installs it for you, it has to be created in the
    /// target's own working directory.
    /// </remarks>
    private static EnvironmentCheckFix? DescribeRuntimeFix(
        RuntimeRequirement requirement,
        Func<string, string, object?[], string>? formatMessage)
    {
        string kind = requirement.Kind.Trim().ToLowerInvariant();
        return kind switch
        {
            "node" => new EnvironmentCheckFix(
                FormatMessage(formatMessage, "Localserver_InstallNode", "Install the Node.js LTS runtime."),
                "winget install OpenJS.NodeJS.LTS"),
            "python" => new EnvironmentCheckFix(
                FormatMessage(formatMessage, "Localserver_InstallPython", "Install Python 3.12."),
                "winget install Python.Python.3.12"),
            "venv" => new EnvironmentCheckFix(
                FormatMessage(formatMessage, "Localserver_CreateVirtualEnvironment", "Create the virtual environment in this target's working directory."),
                "python -m venv venv"),
            "conda" => new EnvironmentCheckFix(
                FormatMessage(formatMessage, "Localserver_InstallMiniconda", "Install Miniconda."),
                "winget install Anaconda.Miniconda3"),
            "cuda" or "gpu" => new EnvironmentCheckFix(
                FormatMessage(formatMessage, "Localserver_RepairGpuDriver",
                    "nvidia-smi ships with the NVIDIA display driver. Install or repair the driver, or clear this requirement if the target does not need a GPU.")),
            _ => null,
        };
    }

    internal static string FormatMessage(
        Func<string, string, object?[], string>? formatMessage,
        string resourceKey,
        string fallback,
        params object?[] arguments)
    {
        try
        {
            string? localized = formatMessage?.Invoke(resourceKey, fallback, arguments);
            if (!string.IsNullOrWhiteSpace(localized)) return localized;
        }
        catch
        {
            // A missing or invalid translation must not interrupt service operations.
        }

        return string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, arguments);
    }

    private static string ResolveExecutable(ServiceDefinition definition) =>
        Path.IsPathRooted(definition.Executable)
            ? definition.Executable
            : Path.GetFullPath(definition.Executable, definition.Cwd);

    private static string? ResolveRuntimeExecutable(
        ServiceDefinition definition,
        RuntimeRequirement requirement)
    {
        if (!string.IsNullOrWhiteSpace(requirement.Path))
        {
            return Path.IsPathRooted(requirement.Path)
                ? requirement.Path
                : Path.GetFullPath(requirement.Path, definition.Cwd);
        }

        string kind = requirement.Kind.Trim().ToLowerInvariant();
        if (kind == "node")
        {
            // Some services ship Node with the application instead of adding it
            // to the hub process PATH.  DeepSeek's launcher does exactly this in
            // env.cmd, so the environment check must probe the same portable
            // runtime before falling back to a machine-wide command.
            string bundledNode = Path.Combine(definition.Cwd, "runtime", "node", "node.exe");
            if (File.Exists(bundledNode))
            {
                return bundledNode;
            }
        }

        if (kind == "venv")
        {
            // A venv requirement wants the venv's Python, not the system one. The
            // standard layout puts python.exe at <cwd>/venv/Scripts/python.exe on
            // Windows and <cwd>/venv/bin/python on Unix. Probing the Windows path
            // first (this is a Windows-only class), then Unix for WSL scenarios.
            string venvPythonWin = Path.Combine(definition.Cwd, "venv", "Scripts", "python.exe");
            if (File.Exists(venvPythonWin))
            {
                return venvPythonWin;
            }

            string venvPythonUnix = Path.Combine(definition.Cwd, "venv", "bin", "python");
            if (File.Exists(venvPythonUnix))
            {
                return venvPythonUnix;
            }

            // No standard venv layout found. Rather than falling back to system
            // Python (which would report green for a missing venv), return a sentinel
            // path that does not exist so CheckRuntimeCoreAsync reports "not found"
            // (lines 197-200 handle the explicit-path case; this is the implicit one).
            return Path.Combine(definition.Cwd, "venv", "Scripts", "python.exe");
        }

        return kind switch
        {
            "node" => "node",
            "python" or "venv" => "python",
            "conda" => "conda",
            "cuda" or "gpu" => "nvidia-smi",
            _ => null,
        };
    }

    private static bool TryParseVersion(string text, out Version? version)
    {
        Match match = VersionPattern().Match(text);
        if (!match.Success)
        {
            version = null;
            return false;
        }

        string[] parts = match.Value.Split('.');
        version = new Version(
            int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0,
            parts.Length > 2 ? int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0);
        return true;
    }

    [GeneratedRegex(@"(?<!\d)\d+(?:\.\d+){0,3}(?!\d)")]
    private static partial Regex VersionPattern();
}
