using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LocalServerHub.Windows;

public sealed record GpuTelemetry(
    string Name,
    string DriverVersion,
    int? UtilizationPercent,
    long? UsedMemoryMiB,
    long? TotalMemoryMiB,
    int? TemperatureCelsius);

public sealed record GpuSnapshot(
    IReadOnlyList<GpuTelemetry> Devices,
    string? Error)
{
    public GpuTelemetry? Primary => Devices.FirstOrDefault();
}

/// <summary>
/// Reads live NVIDIA GPU telemetry without blocking the UI thread. The registry
/// fallback remains responsible for showing a model on machines without NVIDIA
/// telemetry.
/// </summary>
public static class GpuInspector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static async Task<GpuSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "nvidia-smi",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--query-gpu=name,driver_version,utilization.gpu,memory.used,memory.total,temperature.gpu");
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return new([], "nvidia-smi did not start.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException)
        {
            return new([], "NVIDIA telemetry unavailable: nvidia-smi was not found or could not start.");
        }

        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new([], string.IsNullOrWhiteSpace(error)
                    ? $"nvidia-smi exited with code {process.ExitCode}."
                    : error.Trim());
            }

            string output = await outputTask.ConfigureAwait(false);
            List<GpuTelemetry> devices = [];
            foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParse(line, out GpuTelemetry? telemetry) && telemetry is not null)
                {
                    devices.Add(telemetry);
                }
            }

            return devices.Count == 0
                ? new([], "nvidia-smi returned no readable GPU telemetry.")
                : new(devices, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new([], "NVIDIA telemetry probe timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new([], $"NVIDIA telemetry failed: {ex.Message}");
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

    private static bool TryParse(string line, out GpuTelemetry? telemetry)
    {
        string[] fields = line.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length < 6 || string.IsNullOrWhiteSpace(fields[0]))
        {
            telemetry = null;
            return false;
        }

        telemetry = new GpuTelemetry(
            fields[0],
            fields[1],
            ParseInt(fields[2]),
            ParseLong(fields[3]),
            ParseLong(fields[4]),
            ParseInt(fields[5]));
        return true;
    }

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;

    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : null;
}
