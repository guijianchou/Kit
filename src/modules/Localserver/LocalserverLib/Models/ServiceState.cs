namespace LocalServerHub.Core.Models;

/// <summary>
/// Lifecycle states of a managed service. See plan.md §5.
/// </summary>
/// <remarks>
/// The UI never invents a state: it only ever renders what the supervisor has
/// published. A click on Start moves a row to <see cref="Preflight"/>, not to
/// <see cref="Running"/>.
/// </remarks>
public enum ServiceState
{
    /// <summary>Not running. The only state in which the definition may be edited.</summary>
    Stopped,

    /// <summary>Checking runtimes, ports and paths before anything is spawned.</summary>
    Preflight,

    /// <summary>Process spawned; waiting for the health check to pass.</summary>
    Starting,

    /// <summary>Health check passed.</summary>
    Running,

    /// <summary>Graceful stop in progress.</summary>
    Stopping,

    /// <summary>The process exited on its own with a non-zero code.</summary>
    Crashed,

    /// <summary>Waiting out the restart backoff before the next attempt.</summary>
    Backoff,

    /// <summary>Preflight rejected the start, or the process never became healthy.</summary>
    Failed,

    /// <summary>Graceful stop timed out and the kill escalation also failed.</summary>
    StopFailed,
}

public static class ServiceStateExtensions
{
    /// <summary>
    /// True while the supervisor is mid-transition. The row's toggle and every
    /// parameter editor are locked in these states (plan.md §13.8 rule 2).
    /// </summary>
    public static bool IsTransitional(this ServiceState state) =>
        state is ServiceState.Preflight or ServiceState.Starting or ServiceState.Stopping;

    /// <summary>True when a process is alive or about to be.</summary>
    public static bool IsActive(this ServiceState state) =>
        state is ServiceState.Preflight or ServiceState.Starting
            or ServiceState.Running or ServiceState.Stopping or ServiceState.Backoff or ServiceState.StopFailed;

    /// <summary>True when the state represents a problem the user should see.</summary>
    public static bool IsFaulted(this ServiceState state) =>
        state is ServiceState.Crashed or ServiceState.Failed or ServiceState.StopFailed;

    /// <summary>
    /// Configuration is frozen for the duration of a run (plan.md §13.8 rule 3).
    /// </summary>
    public static bool AllowsEditing(this ServiceState state) =>
        state is ServiceState.Stopped or ServiceState.Failed or ServiceState.Crashed;

    public static bool CanStart(this ServiceState state) =>
        state is ServiceState.Stopped or ServiceState.Failed or ServiceState.Crashed;

    public static bool CanStop(this ServiceState state) =>
        state is ServiceState.Running or ServiceState.Starting or ServiceState.Backoff or ServiceState.StopFailed;

    /// <summary>Short label shown next to the status dot.</summary>
    public static string ToDisplayLabel(this ServiceState state, bool? healthProbePassed = null) =>
        state == ServiceState.Running && healthProbePassed == false ? "Unhealthy" : state switch
    {
        ServiceState.Stopped => "Stopped",
        ServiceState.Preflight => "Preflight",
        ServiceState.Starting => "Starting",
        ServiceState.Running => "Running",
        ServiceState.Stopping => "Stopping",
        ServiceState.Crashed => "Crashed",
        ServiceState.Backoff => "Backoff",
        ServiceState.Failed => "Failed",
        ServiceState.StopFailed => "Stop failed",
        _ => state.ToString(),
    };

    /// <summary>
    /// Semantic colour bucket. Mapped to Fluent SystemFill brushes by the UI so
    /// the whole app has exactly one state-to-colour table (plan.md §13.5).
    /// </summary>
    public static ServiceSeverity ToSeverity(this ServiceState state, bool? healthProbePassed = null) =>
        state == ServiceState.Running && healthProbePassed == false ? ServiceSeverity.Critical : state switch
    {
        ServiceState.Running => ServiceSeverity.Success,
        ServiceState.Preflight or ServiceState.Starting
            or ServiceState.Stopping or ServiceState.Backoff => ServiceSeverity.Caution,
        ServiceState.Crashed or ServiceState.Failed or ServiceState.StopFailed => ServiceSeverity.Critical,
        _ => ServiceSeverity.Neutral,
    };
}

public enum ServiceSeverity
{
    Neutral,
    Success,
    Caution,
    Critical,
}
