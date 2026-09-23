using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kit.LocalserverWorker;
using LocalServerHub.Core.Models;
using LocalServerHub.Windows;
using LocalserverLib.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Localserver.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class ServiceLifecycleTests
{
    [TestMethod]
    public async Task StartStopAndRestartReleaseOnlyOwnedProcesses()
    {
        using var fixture = await ServiceFixture.CreateAsync();
        using var runner = new ServiceRunner(fixture.Definition);
        await runner.StartAsync();
        Assert.AreEqual(ServiceState.Running, runner.State);
        Process first = fixture.Track(runner.ProcessId!.Value);
        await runner.RestartAsync();
        Assert.AreEqual(ServiceState.Running, runner.State);
        Process second = fixture.Track(runner.ProcessId!.Value);
        Assert.IsTrue(first.HasExited);
        Assert.AreNotEqual(first.Id, second.Id);
        await runner.StopAsync();
        await runner.StopAsync();
        Assert.IsTrue(second.HasExited);
        Assert.AreEqual(ServiceState.Stopped, runner.State);
        Assert.IsFalse(File.Exists(fixture.OwnershipPath));
    }

    [TestMethod]
    public async Task StopHonorsConfiguredGracefulDeadline()
    {
        using var fixture = await ServiceFixture.CreateAsync("graceful");
        using var runner = new ServiceRunner(fixture.Definition with { StopTimeoutSec = 3 });
        await runner.StartAsync();
        fixture.Track(runner.ProcessId!.Value);
        await runner.StopAsync();
        Assert.AreEqual(ServiceState.Stopped, runner.State);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "flushed")), "The service was killed before its configured graceful shutdown deadline.");
    }

    [TestMethod]
    public async Task StopReapsTheOwnedChildTree()
    {
        using var fixture = await ServiceFixture.CreateAsync("tree");
        using var runner = new ServiceRunner(fixture.Definition);
        await runner.StartAsync();
        fixture.Track(runner.ProcessId!.Value);
        string childFile = Path.Combine(fixture.Root, "child.pid");
        await ServiceFixture.UntilAsync(() => File.Exists(childFile));
        Process child = fixture.Track(int.Parse(await File.ReadAllTextAsync(childFile), System.Globalization.CultureInfo.InvariantCulture));
        await runner.StopAsync();
        Assert.AreEqual(ServiceState.Stopped, runner.State);
        Assert.IsTrue(child.HasExited, "An owned child survived Stop.");
    }

    [TestMethod]
    public async Task ClosingTheManagerPreservesAndRecoversAnEstablishedService()
    {
        using var fixture = await ServiceFixture.CreateAsync();
        var runner = new ServiceRunner(fixture.Definition);
        await runner.StartAsync();
        Process process = fixture.Track(runner.ProcessId!.Value);
        runner.Dispose();
        runner.Dispose();
        Assert.IsFalse(process.HasExited);
        using var recovered = new ServiceRunner(fixture.Definition);
        Assert.IsTrue(recovered.TryRecover());
        Assert.IsTrue(recovered.IsRecovered);
        await recovered.StopAsync();
        Assert.IsTrue(process.HasExited);
        Assert.AreEqual(ServiceState.Stopped, recovered.State);
    }

    [TestMethod]
    public async Task StopCancelsReadinessWithoutRestarting()
    {
        using var fixture = await ServiceFixture.CreateAsync();
        using var runner = new ServiceRunner(fixture.Definition with
        {
            Health = new HealthCheck { Kind = HealthCheckKind.LogPattern, Pattern = "never-ready", TimeoutSec = 20, IntervalSec = 1 },
            Restart = new RestartSettings { Policy = RestartPolicy.Always, MaxRetries = 2, BackoffSec = [1] },
        });
        Task starting = runner.StartAsync();
        await ServiceFixture.UntilAsync(() => runner.ProcessId.HasValue);
        Process process = fixture.Track(runner.ProcessId!.Value);
        await runner.StopAsync();
        try
        {
            await starting;
        }
        catch (OperationCanceledException)
        {
        }

        await Task.Delay(1200);
        Assert.IsTrue(process.HasExited);
        Assert.AreEqual(ServiceState.Stopped, runner.State);
        Assert.IsNull(runner.ProcessId);
    }

    [TestMethod]
    public async Task DisposingDuringReadinessCompletesPendingStartup()
    {
        using var fixture = await ServiceFixture.CreateAsync();
        var runner = new ServiceRunner(fixture.Definition with
        {
            Health = new HealthCheck { Kind = HealthCheckKind.LogPattern, Pattern = "never-ready", TimeoutSec = 20, IntervalSec = 1 },
        });
        Task starting = runner.StartAsync();
        await ServiceFixture.UntilAsync(() => runner.ProcessId.HasValue);
        Process process = fixture.Track(runner.ProcessId!.Value);
        runner.Dispose();
        try
        {
            await starting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }

        await ServiceFixture.UntilAsync(() => process.HasExited);
        Assert.IsFalse(File.Exists(fixture.OwnershipPath));
    }

    [TestMethod]
    public async Task InvalidConfigurationNeverStartsAProcess()
    {
        using var fixture = await ServiceFixture.CreateAsync();
        using var runner = new ServiceRunner(fixture.Definition with { Executable = Path.Combine(fixture.Root, "missing.exe") });
        await runner.StartAsync();
        Assert.AreEqual(ServiceState.Failed, runner.State);
        Assert.IsNull(runner.ProcessId);
        Assert.IsFalse(File.Exists(fixture.OwnershipPath));
    }

    [TestMethod]
    public async Task SupervisorShutdownSequenceStopsEveryRunningService()
    {
        // Mirrors the worker's exit sequence: a final recovery pass adopts services the
        // Settings page may have started, then StopAllAsync terminates every supervised
        // tree. No exit path (module disabled, parent exited, or shutdown requested) may
        // leave an orphaned process behind.
        using var fixture = await ServiceFixture.CreateAsync();
        string dataDirectory = Path.Combine(fixture.Root, "hub-data");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(
            Path.Combine(dataDirectory, "services.json"),
            JsonSerializer.Serialize(new
            {
                services = new[]
                {
                    new
                    {
                        id = fixture.Definition.Id,
                        name = fixture.Definition.Name,
                        cwd = fixture.Definition.Cwd,
                        executable = fixture.Definition.Executable,
                        baseArgs = fixture.Definition.BaseArgs,
                        isEnabled = true,
                    },
                },
            }));

        using var supervisor = new ServiceSupervisor(dataDirectory);
        await supervisor.LoadAsync();
        Assert.AreEqual(1, supervisor.SupervisedCount);

        Assert.IsTrue(await supervisor.StartServiceAsync(fixture.Definition.Id), "The supervised service should start.");
        Assert.IsTrue(File.Exists(fixture.OwnershipPath), "Starting must create an ownership record.");
        int processId = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.OwnershipPath))
            .RootElement.GetProperty("processId")
            .GetInt32();
        Process process = fixture.Track(processId);
        Assert.IsFalse(process.HasExited);

        await supervisor.RecoverRunningServicesAsync();
        await supervisor.StopAllAsync();

        Assert.IsTrue(process.HasExited, "StopAllAsync must terminate every supervised service.");
        Assert.AreEqual("Stopped", supervisor.Describe().Single().State);
        Assert.IsFalse(File.Exists(fixture.OwnershipPath), "Ownership must be cleared after stop.");
    }
}

internal sealed class ServiceFixture : IDisposable
{
    private readonly List<Process> _processes = [];
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "KitLocalserverTests", Guid.NewGuid().ToString("N"));
    internal ServiceDefinition Definition { get; private set; } = null!;
    internal string OwnershipPath => Path.Combine(
        LocalserverPathHelper.StateDirectory,
        $"service-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Definition.Id)).AsSpan(0, 16))}.json");

    internal static async Task<ServiceFixture> CreateAsync(string mode = "normal")
    {
        var fixture = new ServiceFixture();
        Directory.CreateDirectory(fixture.Root);
        string sourcePath = Path.Combine(fixture.Root, "worker.cs");
        string executablePath = Path.Combine(fixture.Root, "worker.exe");
        await File.WriteAllTextAsync(sourcePath, Source);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/nologo", "/target:exe", "/out:" + executablePath, sourcePath })
        {
            start.ArgumentList.Add(argument);
        }

        using Process compiler = Process.Start(start)!;
        Task<string> output = compiler.StandardOutput.ReadToEndAsync();
        Task<string> error = compiler.StandardError.ReadToEndAsync();
        await compiler.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, compiler.ExitCode, (await output) + (await error));
        fixture.Definition = new ServiceDefinition
        {
            Id = "kit-review-" + Path.GetFileName(fixture.Root),
            Name = "Synthetic lifecycle fixture",
            Cwd = fixture.Root,
            Executable = executablePath,
            BaseArgs = [mode],
            Encoding = "utf-8",
            StopTimeoutSec = 1,
            Health = new HealthCheck { Kind = HealthCheckKind.None },
            Restart = new RestartSettings { Policy = RestartPolicy.Never },
        };
        Assert.IsFalse(File.Exists(fixture.OwnershipPath), "A unique fixture must not overwrite an ownership record.");
        return fixture;
    }

    internal Process Track(int processId)
    {
        var process = Process.GetProcessById(processId);
        Assert.IsTrue(string.Equals(Path.Combine(Root, "worker.exe"), process.MainModule!.FileName, StringComparison.OrdinalIgnoreCase));
        _ = process.Handle;
        _processes.Add(process);
        return process;
    }

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    public void Dispose()
    {
        foreach (Process process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        if (Definition is not null)
        {
            File.Delete(OwnershipPath);
        }

        string boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KitLocalserverTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(Root), "N", out _))
        {
            throw new InvalidOperationException("Fixture cleanup escaped its temporary directory.");
        }

        // Windows can release an executable mapping shortly after signaling exit.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                break;
            }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private const string Source = """
        using System;
        using System.Diagnostics;
        using System.IO;
        using System.Threading;
        class Worker
        {
            static void Main(string[] args)
            {
                string mode = args.Length == 0 ? "normal" : args[0];
                if (mode == "tree")
                {
                    var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "child");
                    start.UseShellExecute = false;
                    start.CreateNoWindow = true;
                    var child = Process.Start(start);
                    File.WriteAllText("child.pid", child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                Console.WriteLine("fixture-started");
                Console.Out.Flush();
                if (mode == "graceful")
                {
                    Console.In.ReadToEnd();
                    Thread.Sleep(1400);
                    File.WriteAllText("flushed", "complete");
                    return;
                }
                Thread.Sleep(60000);
            }
        }
        """;
}
