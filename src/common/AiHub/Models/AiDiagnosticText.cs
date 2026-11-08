namespace Kit.AiHub.Models;

using System;
using System.Globalization;
using Kit.AiHub.Storage;

/// <summary>Formats safe native execution metadata without displaying raw provider or model text.</summary>
public static class AiDiagnosticText
{
    public static string Format(AiRouteDiagnostics? diagnostics)
    {
        if (diagnostics is null)
        {
            return string.Empty;
        }

        AiRouteDiagnostics safe = AiDiagnosticsStore.NormalizeDiagnostics(diagnostics);
        string status = safe.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        string retryLimit = safe.NativeRetryLimit?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        string exitCode = safe.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        string context = safe.ContextWindowTokens > 0 ? safe.ContextWindowTokens.ToString(CultureInfo.InvariantCulture) : "unknown";
        return FormattableString.Invariant(
            $"phase={safe.Phase}, reason={safe.Reason}, httpStatus={status}, nativeRetries={safe.NativeRetryCount}/{retryLimit}, cancellation={safe.CancellationSource}, processFailure={safe.ProcessFailure}, exitCode={exitCode}, contextTokens={context}, elapsedMs={safe.ElapsedMilliseconds}");
    }
}
