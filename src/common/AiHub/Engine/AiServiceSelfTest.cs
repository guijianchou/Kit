namespace Kit.AiHub.Engine;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Security;
using Kit.AiHub.Serialization;

/// <summary>Input record for the AI service self-test.</summary>
public sealed class SelfTestInput
{
    public string Id { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Outcome of a self-test run, suitable for logging or display.
/// </summary>
/// <param name="Success">True when the service answered with a valid result.</param>
/// <param name="Readiness">State of the service before the run.</param>
/// <param name="Elapsed">Wall-clock duration of the call.</param>
/// <param name="UsedRoute">Route that served the request.</param>
/// <param name="UsedModel">Model reported by the kernel.</param>
/// <param name="FindingCount">Number of validated findings returned.</param>
/// <param name="ErrorMessage">Failure detail, or null on success.</param>
public sealed record SelfTestResult(
    bool Success,
    AiReadiness Readiness,
    TimeSpan Elapsed,
    string UsedRoute,
    string UsedModel,
    int FindingCount,
    string? ErrorMessage)
{
    /// <summary>One-line summary for a log or status bar.</summary>
    public string Summary => Success
        ? $"Self-test passed in {Elapsed.TotalMilliseconds:F0} ms via {UsedRoute} ({UsedModel}), {FindingCount} finding(s)."
        : $"Self-test failed after {Elapsed.TotalMilliseconds:F0} ms: {ErrorMessage}";
}

/// <summary>
/// Exercises the shared AI service end to end.
/// </summary>
/// <remarks>
/// Runs a single trivial probe through the same engine, policy and validation path a plugin
/// uses. Success verifies this route and probe contract at this moment; it does not prove
/// that other plugin inputs, policies or routes will succeed.
/// </remarks>
public static class AiServiceSelfTest
{
    /// <summary>The probe record the chain expects.</summary>
    private const string ProbeNote = "ping";

    /// <summary>
    /// Runs the self-test against the shared engine.
    /// </summary>
    /// <param name="engine">Engine to test; defaults to the shared instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<SelfTestResult> RunAsync(
        IAiTaskEngine? engine = null,
        CancellationToken cancellationToken = default)
    {
        engine ??= AiHubEngine.Current;
        var stopwatch = Stopwatch.StartNew();

        // Report readiness first: when the service is off or unconfigured this is the real
        // answer, and it costs nothing because the verdict is cached.
        AiReadiness readiness = engine is TaskAiEngine concrete
            ? concrete.GetReadiness()
            : new AiReadiness(engine.IsEnabled ? AiReadinessLevel.Unverified : AiReadinessLevel.Disabled, engine.ActiveKernel, string.Empty);

        if (!readiness.CanAttempt)
        {
            return new SelfTestResult(false, readiness, stopwatch.Elapsed, string.Empty, string.Empty, 0,
                readiness.Detail ?? "The AI service is not available.");
        }

        var items = new List<SelfTestInput>
        {
            new() { Id = "self-test-1", Note = ProbeNote },
            new() { Id = "self-test-2", Note = "ignore me" },
        };

        try
        {
            AiTaskResult<AiTaskReport> result = await engine.ExecuteTaskAsync(
                SelfTestPolicy.PluginId,
                SelfTestPolicy.TaskId,
                items,
                CreateSchema(),
                new AiTaskOptions { TimeoutSeconds = 120, Language = "en-US", BatchSize = 2 },
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            if (!result.IsSuccess)
            {
                return new SelfTestResult(false, readiness, stopwatch.Elapsed, result.UsedRoute, result.UsedModel, 0,
                    $"{result.ErrorCode}: {result.ErrorMessage}");
            }

            int findings = result.Payload?.Findings?.Count ?? 0;
            if (result.Payload is null || !ValidateProbe(result.Payload))
            {
                // A successful transport still needs the exact diagnostic response contract.
                return new SelfTestResult(false, readiness, stopwatch.Elapsed, result.UsedRoute, result.UsedModel, 0,
                    "The service answered but did not return exactly the ping finding with no actions; the self-test instructions were not followed.");
            }

            return new SelfTestResult(true, readiness, stopwatch.Elapsed, result.UsedRoute, result.UsedModel, findings, null);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new SelfTestResult(false, readiness, stopwatch.Elapsed, string.Empty, string.Empty, 0, "Cancelled.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SelfTestResult(false, readiness, stopwatch.Elapsed, string.Empty, string.Empty, 0, ex.Message);
        }
    }

    private static AiTaskSchema<SelfTestInput, AiTaskReport> CreateSchema()
    {
        var shared = AiTaskReport.CreateSchema(AiHubJsonContext.Default.SelfTestInput);
        return new()
        {
            InputTypeInfo = shared.InputTypeInfo,
            OutputTypeInfo = shared.OutputTypeInfo,
            AllowedActions = new HashSet<string>(StringComparer.Ordinal),
            ValidateOutput = (report, ids) => shared.ValidateOutput(report, ids) && ValidateProbe(report),
        };
    }

    private static bool ValidateProbe(AiTaskReport report) => report.Findings is { Count: 1 }
        && report.Actions is { Count: 0 }
        && report.Findings[0] is { ItemId: "item-000001", Key: "self-test", Severity: "low" } finding
        && new[] { finding.TitleEn, finding.TitleZh, finding.DescriptionEn, finding.DescriptionZh, finding.RecommendationEn, finding.RecommendationZh }
            .All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl));
}
