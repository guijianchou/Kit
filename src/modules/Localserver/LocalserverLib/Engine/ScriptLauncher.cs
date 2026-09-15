using System.Diagnostics;
using LocalServerHub.Core.Models;

namespace LocalServerHub.Windows;

/// <summary>
/// How one configured executable actually reaches CreateProcess.
/// </summary>
/// <param name="FileName">The image Windows is asked to run.</param>
/// <param name="PrefixArguments">
/// Tokens that must precede the service's own arguments, e.g. the script path when
/// an interpreter is doing the launching.
/// </param>
/// <param name="Interpreter">
/// Null when the target was directly executable. Otherwise the interpreter this
/// resolution introduced, for diagnostics and for the command shown in the UI.
/// </param>
public sealed record ScriptLaunch(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    string? Interpreter)
{
    /// <summary>True when a non-PE target is being run through an interpreter.</summary>
    public bool IsIndirect => Interpreter is not null;
}

/// <summary>
/// Maps a configured target onto something Windows can actually spawn.
/// </summary>
/// <remarks>
/// <c>UseShellExecute = false</c> is required for redirected stdio, and in that mode
/// CreateProcess only accepts PE images. A .cmd, .py or .ps1 handed over directly
/// fails deep in the OS with a bare Win32 error, so every non-PE target is routed
/// through its interpreter here instead. This is the one place that knows the
/// mapping: both <see cref="ServiceRunner"/> and its preflight consult it, so the
/// check and the launch can never disagree.
/// </remarks>
public static class ScriptLauncher
{
    private const string CmdExe = "cmd.exe";

    public static ScriptLaunch ResolveForService(ServiceDefinition definition, string resolvedExecutable)
    {
        string extension = Path.GetExtension(resolvedExecutable).ToLowerInvariant();
        string? kind = extension switch
        {
            ".js" or ".mjs" or ".cjs" => "node",
            ".py" or ".pyw" => "python",
            _ => null,
        };
        RuntimeRequirement? runtime = kind is null ? null : definition.Runtimes.FirstOrDefault(item =>
            string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)
            || (kind == "python" && string.Equals(item.Kind, "py", StringComparison.OrdinalIgnoreCase)));
        string? interpreter = string.IsNullOrWhiteSpace(runtime?.Path)
            ? null
            : Path.GetFullPath(runtime.Path, Path.GetFullPath(definition.Cwd));
        if (interpreter is null && kind == "node")
        {
            string bundled = Path.GetFullPath(Path.Combine(definition.Cwd, "runtime", "node", "node.exe"));
            if (File.Exists(bundled)) interpreter = bundled;
        }
        return Resolve(resolvedExecutable, interpreter);
    }

    /// <summary>Extensions that need an interpreter, and the argv prefix each one needs.</summary>
    private static readonly Dictionary<string, (string Interpreter, string[] Prefix)> Interpreters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // The command processor: /d skips AutoRun, /s keeps the quoting rules
            // predictable, /c runs and exits.
            [".cmd"] = (CmdExe, ["/d", "/s", "/c"]),
            [".bat"] = (CmdExe, ["/d", "/s", "/c"]),
            // "py" is the launcher shipped with Windows Python; it honours shebang
            // lines and per-machine version pinning, which python.exe does not.
            [".py"] = ("py.exe", []),
            [".pyw"] = ("py.exe", []),
            // -NoProfile keeps a user's profile from changing how a service starts;
            // Bypass applies to this invocation only and does not touch machine policy.
            [".ps1"] = ("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]),
            [".js"] = ("node.exe", []),
            [".mjs"] = ("node.exe", []),
            [".cjs"] = ("node.exe", []),
        };

    /// <summary>
    /// Decides how <paramref name="resolvedExecutable"/> should be spawned. The path
    /// is expected to be already resolved against the service's working directory.
    /// </summary>
    public static ScriptLaunch Resolve(string resolvedExecutable, string? interpreterPath = null)
    {
        if (string.IsNullOrWhiteSpace(resolvedExecutable))
        {
            return new ScriptLaunch(resolvedExecutable ?? string.Empty, [], null);
        }

        string extension = Path.GetExtension(resolvedExecutable);
        if (extension.Length == 0
            || !Interpreters.TryGetValue(extension, out (string Interpreter, string[] Prefix) mapping))
        {
            return new ScriptLaunch(resolvedExecutable, [], null);
        }

        // The script path always follows whatever switches the interpreter needs.
        string interpreter = string.IsNullOrWhiteSpace(interpreterPath)
            ? mapping.Interpreter
            : interpreterPath;
        List<string> prefix = [.. mapping.Prefix, resolvedExecutable];
        return new ScriptLaunch(interpreter, prefix, interpreter);
    }

    /// <summary>
    /// Whether the interpreter this target needs can be found. Returns null when the
    /// target is clear to start, or a human-readable reason when it is not.
    /// </summary>
    public static string? CheckInterpreterAvailable(ScriptLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        if (!launch.IsIndirect || ResolveOnPath(launch.FileName) is not null)
        {
            return null;
        }

        return Path.IsPathRooted(launch.FileName)
            ? $"Configured interpreter was not found: {launch.FileName}"
            : $"{launch.FileName} is required to run this target but was not found on PATH.";
    }

    /// <summary>Finds an executable on PATH, mirroring how CreateProcess searches.</summary>
    private static string? ResolveOnPath(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            return File.Exists(fileName) ? fileName : null;
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        // PATHEXT matters for "node" -> "node.cmd" on some installs; an explicit
        // extension is tried as-is first.
        string[] extensions = Path.HasExtension(fileName)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string directory in pathVariable.Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, fileName + extension);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is skipped rather than fatal.
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Applies the resolution to a start info, prefix arguments included.</summary>
    public static void Apply(ProcessStartInfo startInfo, ScriptLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(launch);

        startInfo.FileName = launch.FileName;
        foreach (string argument in launch.PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }
}
