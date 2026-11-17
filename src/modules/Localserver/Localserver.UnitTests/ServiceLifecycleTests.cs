using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kit.LocalserverWorker;
using LocalServerHub.Core.Logging;
using LocalServerHub.Core.Models;
using LocalServerHub.Windows;
using LocalServerHub.Windows.Native;
using LocalserverLib.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Localserver.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class ServiceLifecycleTests
{
    [TestMethod]
    public async Task FirstInstructionAndImmediateChildBelongToTheOwnershipJob()
    {
        using var fixture = await ServiceFixture.CreateAsync("tree");
        using var runner = new ServiceRunner(fixture.Definition);
        await runner.StartAsync();
        Assert.AreEqual(ServiceState.Running, runner.State, runner.LastError);
        fixture.Track(runner);
        string rootMarker = Path.Combine(fixture.Root, "root.job");
        string childMarker = Path.Combine(fixture.Root, "child.job");
        await ServiceFixture.UntilAsync(() => File.Exists(rootMarker) && File.Exists(childMarker) && File.Exists(Path.Combine(fixture.Root, "child.pid")));
        Assert.AreEqual("True", await File.ReadAllTextAsync(rootMarker));
        Assert.AreEqual("True", await File.ReadAllTextAsync(childMarker));
        Process child = fixture.Track(int.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "child.pid")), System.Globalization.CultureInfo.InvariantCulture));
        await runner.StopAsync();
        Assert.IsTrue(child.HasExited, "An immediate descendant escaped the ownership job.");
    }

    [TestMethod]
    public async Task SuspendedLaunchPreservesArgumentsEnvironmentAndRedirectedStreams()
    {
        using var fixture = await ServiceFixture.CreateAsync("contract");
        string[] arguments = ["contract", string.Empty, "with spaces", "quoted\"value", @"C:\ends with slash\", "中文参数"];
        using var runner = new ServiceRunner(fixture.Definition with
        {
            BaseArgs = arguments,
            Env = new Dictionary<string, string> { ["KIT_LAUNCH_TEST"] = "值 with spaces" },
        });
        await runner.StartAsync();
        Assert.AreEqual(ServiceState.Running, runner.State, runner.LastError);
        fixture.Track(runner);
        string argsFile = Path.Combine(fixture.Root, "arguments.txt");
        await ServiceFixture.UntilAsync(() => File.Exists(argsFile) && File.Exists(Path.Combine(fixture.Root, "directory.txt")));
        string[] received = (await File.ReadAllLinesAsync(argsFile)).Select(value => Encoding.UTF8.GetString(Convert.FromBase64String(value))).ToArray();
        CollectionAssert.AreEqual(arguments, received);
        Assert.AreEqual("值 with spaces", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "environment.txt")));
        Assert.AreEqual(fixture.Root, await File.ReadAllTextAsync(Path.Combine(fixture.Root, "directory.txt")));
        await runner.SendInputAsync("输入 with spaces");
        await ServiceFixture.UntilAsync(() => runner.Log.Snapshot().Any(line => line.Stream == LogStream.StdOut && line.Text == "echo:输入 with spaces"));
        Assert.IsTrue(runner.Log.Snapshot().Any(line => line.Stream == LogStream.StdErr && line.Text == "fixture-stderr"));
        await runner.StopAsync();
    }

    [TestMethod]
    public async Task StartStopAndRestartReleaseOnlyOwnedProcesses()
    {
        using var fixture = await ServiceFixture.CreateAsync();
        using var runner = new ServiceRunner(fixture.Definition);
        await runner.StartAsync();
        Assert.AreEqual(ServiceState.Running, runner.State, runner.LastError);
        Process first = fixture.Track(runner);
        await runner.RestartAsync();
        Assert.AreEqual(ServiceState.Running, runner.State, runner.LastError);
        Process second = fixture.Track(runner);
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
        fixture.Track(runner);
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
        fixture.Track(runner);
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
        Process process = fixture.Track(runner);
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
    public async Task LegacyOwnershipWithoutAProcessChainRemainsRecoverable()
    {
        using var fixture = await ServiceFixture.CreateAsync();
        using var runner = new ServiceRunner(fixture.Definition);
        await runner.StartAsync();
        Process process = fixture.Track(runner);
        runner.Dispose();
        var ownership = JsonNode.Parse(await File.ReadAllTextAsync(fixture.OwnershipPath))!.AsObject();
        Assert.IsTrue(ownership.Remove("processChain"));
        await File.WriteAllTextAsync(fixture.OwnershipPath, ownership.ToJsonString());

        using var recovered = new ServiceRunner(fixture.Definition);
        Assert.IsTrue(recovered.TryRecover(), "Legacy ownership must preserve the named job and process identity.");
        await recovered.StopAsync();
        Assert.IsTrue(process.HasExited);
        Assert.IsFalse(File.Exists(fixture.OwnershipPath));
    }

    [TestMethod]
    public async Task RecoveryWithoutOwnershipDoesNotScanProcesses()
    {
        using var fixture = await ServiceFixture.CreateAsync();
        using var runner = new ServiceRunner(fixture.Definition);
        var snapshot = new Lazy<ProcessTreeSnapshot>(() => throw new InvalidOperationException("An idle service must not scan processes."));

        Assert.IsFalse(runner.TryRecover(snapshot));
        Assert.IsFalse(snapshot.IsValueCreated);
    }

    [TestMethod]
    public async Task MultipleRecoveriesShareOneProcessScan()
    {
        using var first = await ServiceFixture.CreateAsync();
        using var second = await ServiceFixture.CreateAsync();
        using var firstOwner = new ServiceRunner(first.Definition);
        using var secondOwner = new ServiceRunner(second.Definition);
        await firstOwner.StartAsync();
        await secondOwner.StartAsync();
        Process firstProcess = first.Track(firstOwner);
        Process secondProcess = second.Track(secondOwner);
        firstOwner.Dispose();
        secondOwner.Dispose();
        int scans = 0;
        var snapshot = new Lazy<ProcessTreeSnapshot>(() =>
        {
            scans++;
            return ProcessTreeSnapshot.Capture();
        });
        using var firstRecovered = new ServiceRunner(first.Definition);
        using var secondRecovered = new ServiceRunner(second.Definition);

        Assert.IsTrue(firstRecovered.TryRecover(snapshot));
        Assert.IsTrue(secondRecovered.TryRecover(snapshot));
        Assert.AreEqual(1, scans);
        await firstRecovered.StopAsync();
        await secondRecovered.StopAsync();
        Assert.IsTrue(firstProcess.HasExited);
        Assert.IsTrue(secondProcess.HasExited);
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
        Process process = fixture.Track(runner);
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
        Process process = fixture.Track(runner);
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

    [TestMethod]
    public async Task StaleOwnershipRecordIsArchivedOnceItsTreeIsGone()
    {
        using var fixture = await ServiceFixture.CreateAsync();
        var runner = new ServiceRunner(fixture.Definition);
        await runner.StartAsync();
        Process process = fixture.Track(runner);
        runner.Dispose();
        Assert.IsFalse(process.HasExited);
        process.Kill();
        await process.WaitForExitAsync();

        using var recovered = new ServiceRunner(fixture.Definition);
        Assert.IsFalse(recovered.TryRecover(new Lazy<ProcessTreeSnapshot>(ProcessTreeSnapshot.Capture)));
        Assert.IsFalse(File.Exists(fixture.OwnershipPath), "A stale ownership record must be archived once no recorded process is alive.");
        Assert.IsFalse(
            recovered.TryRecover(new Lazy<ProcessTreeSnapshot>(() => throw new InvalidOperationException("An archived record must not be rescanned."))),
            "An archived record must not trigger another scan.");
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

    internal Process Track(ServiceRunner runner)
    {
        int? processId = runner.ProcessId;
        Assert.IsNotNull(processId, runner.LastError);
        return Track(processId.GetValueOrDefault());
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
        using System.Runtime.InteropServices;
        using System.Security.Cryptography;
        using System.Text;
        class Worker
        {
            static void Main(string[] args)
            {
                string mode = args.Length == 0 ? "normal" : args[0];
                string tag = Environment.GetEnvironmentVariable("LOCALSERVERHUB_OWNER_TAG");
                if (tag != null)
                {
                    string id = tag.Split('/')[1];
                    byte[] digest;
                    using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(id + "\n" + tag));
                    string jobName = "Local\\LocalServerHub.Service." + BitConverter.ToString(digest, 0, 16).Replace("-", "");
                    IntPtr job = OpenJobObjectW(4, false, jobName);
                    bool member = false;
                    bool found = job != IntPtr.Zero && IsProcessInJob(Process.GetCurrentProcess().Handle, job, out member);
                    if (job != IntPtr.Zero) CloseHandle(job);
                    WriteText(mode == "child" ? "child.job" : "root.job", (found && member).ToString());
                }
                Console.InputEncoding = new UTF8Encoding(false);
                Console.OutputEncoding = new UTF8Encoding(false);
                if (mode == "contract")
                {
                    File.WriteAllLines("arguments.txt.tmp", Array.ConvertAll(args, value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))));
                    File.Move("arguments.txt.tmp", "arguments.txt");
                    WriteText("environment.txt", Environment.GetEnvironmentVariable("KIT_LAUNCH_TEST"));
                    WriteText("directory.txt", Environment.CurrentDirectory);
                    Console.Error.WriteLine("fixture-stderr");
                    Console.Error.Flush();
                    Console.WriteLine("echo:" + Console.ReadLine());
                    Console.Out.Flush();
                }
                if (mode == "tree")
                {
                    var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "child");
                    start.UseShellExecute = false;
                    start.CreateNoWindow = true;
                    var child = Process.Start(start);
                    WriteText("child.pid", child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                Console.WriteLine("fixture-started");
                Console.Out.Flush();
                if (mode == "graceful")
                {
                    Console.In.ReadToEnd();
                    Thread.Sleep(1400);
                    WriteText("flushed", "complete");
                    return;
                }
                Thread.Sleep(60000);
            }
            static void WriteText(string path, string value)
            {
                File.WriteAllText(path + ".tmp", value);
                File.Move(path + ".tmp", path);
            }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            static extern IntPtr OpenJobObjectW(uint access, bool inherit, string name);
            [DllImport("kernel32.dll")]
            static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool member);
            [DllImport("kernel32.dll")]
            static extern bool CloseHandle(IntPtr handle);
        }
        """;
}
