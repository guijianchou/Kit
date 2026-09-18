namespace Kit.AiHub.UnitTests.Fixtures;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

internal sealed class KernelFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "KitAiHubTests", Guid.NewGuid().ToString("N"));
    internal string Executable => Path.Combine(Root, "synthetic.exe");
    internal string RequestRoot => Path.Combine(Root, "requests");
    internal KernelManagerService Manager => new(Path.Combine(Root, "kernels"));
    internal RouteDispatcher Dispatcher => new(Manager, RequestRoot);

    internal Task SetResponseAsync(string jsonTemplate) => File.WriteAllTextAsync(Path.Combine(Root, "response.json"), jsonTemplate);

    internal static async Task<KernelFixture> CreateAsync()
    {
        var fixture = new KernelFixture();
        Directory.CreateDirectory(fixture.Root);
        string source = Path.Combine(fixture.Root, "synthetic.cs");
        await File.WriteAllTextAsync(source, SyntheticSource, new UTF8Encoding(false));
        string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        Assert.IsTrue(File.Exists(compiler), "The Windows .NET Framework C# compiler is required for synthetic process fixtures.");
        var start = new ProcessStartInfo { FileName = compiler, WorkingDirectory = fixture.Root };
        foreach (string argument in new[] { "/nologo", "/target:exe", "/reference:System.Web.Extensions.dll", "/out:" + fixture.Executable, source })
        {
            start.ArgumentList.Add(argument);
        }

        KernelProcessResult compilation = await KernelProcessRunner.RunAsync(start, null, TimeSpan.FromSeconds(20), CancellationToken.None, 64 * 1024, 64 * 1024);
        Assert.AreEqual(KernelProcessFailure.None, compilation.Failure, "Synthetic fixture compiler did not complete.");
        Assert.AreEqual(0, compilation.ExitCode, "Synthetic fixture source did not compile: " + compilation.StandardOutput);
        foreach (string kernel in new[] { "codex", "pi" })
        {
            string directory = Path.Combine(fixture.Root, "kernels", kernel);
            Directory.CreateDirectory(directory);
            File.Copy(fixture.Executable, Path.Combine(directory, kernel + ".exe"));
            if (kernel == "pi")
            {
                Directory.CreateDirectory(Path.Combine(directory, "theme"));
                await File.WriteAllTextAsync(Path.Combine(directory, "package.json"), "{}");
                await File.WriteAllTextAsync(Path.Combine(directory, "theme", "dark.json"), "{}");
                await File.WriteAllTextAsync(Path.Combine(directory, "theme", "light.json"), "{}");
            }
        }

        return fixture;
    }

    internal ProcessStartInfo Process(string scenario)
    {
        var start = new ProcessStartInfo { FileName = Executable, WorkingDirectory = Root };
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add(Root);
        return start;
    }

    internal static AiTargetSettings Target(string scenario = "success", string mode = "responses") => new()
    {
        Name = "Synthetic",
        IsActive = true,
        BaseUrl = "https://synthetic.invalid",
        ApiKey = "synthetic-fixture-secret",
        Mode = mode,
        Model = "fixture-" + scenario,
        Effort = "max",
    };

    internal async Task WaitForFileAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(Root, name)))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    internal void AssertChildrenExited()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "children.json")));
        foreach (JsonElement value in document.RootElement.EnumerateArray())
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(value.GetInt32());
                Assert.IsTrue(process.HasExited, "A synthetic descendant survived process cancellation.");
            }
            catch (ArgumentException)
            {
                // Windows already reaped the terminated synthetic process.
            }
        }
    }

    public void Dispose()
    {
        string root = Path.GetFullPath(Root);
        string boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KitAiHubTests")) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
        {
            throw new InvalidOperationException("Fixture cleanup escaped its generated directory.");
        }

        if (Directory.Exists(root))
        {
            string childrenPath = Path.Combine(root, "children.json");
            if (File.Exists(childrenPath))
            {
                using JsonDocument children = JsonDocument.Parse(File.ReadAllText(childrenPath));
                foreach (JsonElement value in children.RootElement.EnumerateArray())
                {
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById(value.GetInt32());
                        if (!process.HasExited && process.MainModule?.FileName is { } executable &&
                            Path.GetFullPath(executable).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(5000);
                        }
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Cleanup only targets synthetic processes whose executable is inside this fixture.
                    }
                }
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        break;
                    }

                    foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                        }
                        catch
                        {
                        }
                    }

                    Directory.Delete(root, recursive: true);
                    break;
                }
                catch (Exception) when (attempt < 4)
                {
                    Thread.Sleep(150 * (attempt + 1));
                }
            }
        }
    }

    private const string SyntheticSource = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.IO;
        using System.Linq;
        using System.Text;
        using System.Text.RegularExpressions;
        using System.Threading;
        using System.Web.Script.Serialization;

        internal static class SyntheticKernel
        {
            private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16000000 };
            private static string Arg(string[] args, string key)
            {
                int index = Array.IndexOf(args, key);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : string.Empty;
            }

            private static void Emit(object value)
            {
                Console.WriteLine(Json.Serialize(value));
                Console.Out.Flush();
            }

            private static void SpawnChild(string directory)
            {
                var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                    "--fixture sleep \"" + directory + "\"") { UseShellExecute = false, CreateNoWindow = true };
                var child = Process.Start(start);
                File.WriteAllText(Path.Combine(directory, "children.tmp"), Json.Serialize(new[] { Process.GetCurrentProcess().Id, child.Id }));
                File.Move(Path.Combine(directory, "children.tmp"), Path.Combine(directory, "children.json"));
            }

            private static int Main(string[] args)
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                if (args.Length > 0 && args[0] == "--fixture")
                {
                    string scenario = args[1];
                    string root = args[2];
                    if (scenario == "flood-output" || scenario == "flood-error")
                    {
                        TextWriter writer = scenario == "flood-output" ? Console.Out : Console.Error;
                        for (int index = 0; index < 4096; index++) writer.Write(new string('x', 4096));
                        writer.Flush();
                        return 0;
                    }
                    if (scenario == "drain")
                    {
                        Console.Out.Write(new string('o', 131072));
                        Console.Error.Write(new string('e', 131072));
                        return 0;
                    }
                    if (scenario == "nonzero") { Console.Error.Write("synthetic rejection"); return 7; }
                    if (scenario == "spawn" || scenario == "block-stdin" || scenario == "detach") SpawnChild(root);
                    if (scenario == "detach") return 0;
                    Thread.Sleep(Timeout.Infinite);
                    return 0;
                }
                if (args.Contains("--version")) { Console.WriteLine("codex-cli 1.2.3"); return 0; }
                if (args.Contains("--bundled"))
                {
                    Emit(new { models = new[] { new { slug = "fixture-success", use_responses_lite = true, retained = "capability" } } });
                    return 0;
                }

                bool codex = args.Length > 0 && args[0] == "exec";
                string home = Environment.GetEnvironmentVariable(codex ? "CODEX_HOME" : "PI_CODING_AGENT_DIR");
                string config = File.ReadAllText(Path.Combine(home, codex ? "config.toml" : "models.json"));
                string model = codex
                    ? Json.Deserialize<string>(Regex.Match(config, "(?m)^model = (\"(?:\\\\.|[^\"\\\\])*\")$").Groups[1].Value)
                    : Arg(args, "--model");
                string captureRoot = Directory.GetParent(Environment.CurrentDirectory).FullName;
                string input = codex ? Console.In.ReadToEnd() : File.ReadAllText(args.Last().Substring(1));
                string capture = Json.Serialize(new
                {
                    Args = args,
                    Config = config,
                    Model = model,
                    Input = input,
                    Home = home,
                    SessionHome = Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR"),
                    InheritedKeysAbsent = Environment.GetEnvironmentVariable("OPENAI_API_KEY") == null && Environment.GetEnvironmentVariable("CODEX_API_KEY") == null,
                    KeyMatches = Environment.GetEnvironmentVariable("KIT_AI_API_KEY") == "synthetic-fixture-secret",
                    SecretInArguments = args.Any(value => value.Contains("synthetic-fixture-secret")),
                    SecretInConfig = config.Contains("synthetic-fixture-secret"),
                    Catalog = codex ? File.ReadAllText(Path.Combine(home, "models.json")) : string.Empty
                });
                File.WriteAllText(Path.Combine(captureRoot, "capture-" + Process.GetCurrentProcess().Id + ".json"), capture);
                try { File.WriteAllText(Path.Combine(captureRoot, "capture.json"), capture); }
                catch (IOException) { }

                if (model == "fixture-timeout") { Thread.Sleep(Timeout.Infinite); return 0; }
                string detail = model == "fixture-auth" ? "HTTP 401 synthetic-fixture-secret" :
                    model == "fixture-transport-timeout" ? "HTTP 504 synthetic-fixture-secret" :
                    model == "fixture-transport" ? "HTTP 503 synthetic-fixture-secret" : "synthetic configuration rejection synthetic-fixture-secret";
                if (model == "fixture-auth" || model == "fixture-transport-timeout" || model == "fixture-transport" || model == "fixture-error")
                {
                    if (codex) Emit(new { type = "turn.failed", error = new { message = detail } });
                    else Emit(new { type = "message_end", message = new { role = "assistant", stopReason = "error", errorMessage = detail } });
                    return 0;
                }

                string payload = model == "fixture-nonempty" ? "{\"issues\":[{}]}" :
                    model == "fixture-missing" ? "{}" : model == "fixture-wrongtype" ? "{\"issues\":null}" :
                    model == "fixture-duplicate" ? "{\"issues\":[],\"issues\":[]}" :
                    model == "fixture-invalid-json" ? "not JSON" : "{\"issues\":[]}";
                string responsePath = Path.Combine(Directory.GetParent(captureRoot).FullName, "response.json");
                if (File.Exists(responsePath))
                {
                    string itemId = Regex.Match(input, "\\\"itemId\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"").Groups[1].Value;
                    payload = File.ReadAllText(responsePath).Replace("{{itemId}}", itemId);
                }
                if (codex)
                {
                    if (model == "fixture-tool") Emit(new { type = "item.completed", item = new { type = "command_execution" } });
                    Emit(new { type = "item.completed", item = new { type = "agent_message", text = payload } });
                    File.WriteAllText(Arg(args, "--output-last-message"), model == "fixture-large-file" ? new string('x', 9000000) : payload);
                    if (model != "fixture-incomplete") Emit(new { type = "turn.completed", model = "fixture-actual" });
                }
                else
                {
                    Emit(new
                    {
                        type = "message_end",
                        message = new
                        {
                            role = "assistant", model = "fixture-actual",
                            stopReason = model == "fixture-incomplete" ? "length" : model == "fixture-tool" ? "toolUse" : "stop",
                            content = new[] { new { type = "text", text = payload } }
                        }
                    });
                    Emit(new { type = "agent_end", messages = new object[0] });
                }
                return 0;
            }
        }
        """;
}
