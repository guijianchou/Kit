using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

internal static class DevelopmentCacheScanner
{
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);
    private static readonly string[] Extensions = [".pch", ".pchast", ".obj", ".iobj", ".ilk", ".pyc", ".cache", ".dll", ".exe", ".lib", ".ipdb", ".winmd", ".pri", ".pdb", ".json", ".cs", ".props", ".targets", ".db", ".opendb", ".ipch"];
    private static readonly string[] OutputExtensions = [".dll", ".exe", ".lib", ".ipdb", ".pdb", ".winmd", ".pri"];
    private const string ManifestSuffix = ".FileListAbsolute.txt";

    internal static List<TempFileInfo> Scan(string root, int limit, CancellationToken token)
    {
        var items = new List<TempFileInfo>();
        if (!Directory.Exists(root) || !OptimizationFileSafety.HasNoReparsePoints(root)) return items;
        foreach (string repository in Directory.EnumerateDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            if (!IsRepository(repository)) continue;
            var args = new List<string> { "ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--" };
            args.AddRange(Extensions.Select(extension => "*" + extension));
            args.Add("*" + ManifestSuffix);
            string? output = Git(repository, args, token);
            if (output == null) continue;
            var files = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var manifests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string manifest in files.Where(path => path.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                token.ThrowIfCancellationRequested();
                foreach (string path in ReadBuildManifest(Path.Combine(repository, manifest), repository))
                    manifests.TryAdd(path, Path.Combine(repository, manifest));
            }
            foreach (string relative in files)
            {
                token.ThrowIfCancellationRequested();
                if (items.Count >= limit) return items;
                try
                {
                    var file = new FileInfo(Path.Combine(repository, relative));
                    manifests.TryGetValue(file.FullName, out string? manifest);
                    bool buildOutput = manifest != null && IsBuildOutput(file, repository);
                    if ((!MatchesArtifact(file, repository) && !buildOutput) || !OptimizationFileSafety.IsEligible(file, MinimumAge)) continue;
                    items.Add(new TempFileInfo
                    {
                        FilePath = file.FullName, FileName = file.Name, SizeInBytes = file.Length,
                        LastModified = file.LastWriteTimeUtc, DevelopmentRepository = repository,
                        BuildManifestPath = buildOutput ? manifest! : string.Empty,
                        CacheRoot = GetOutputDirectory(file.FullName, repository) ?? file.DirectoryName!,
                        Category = IsDotNetIntermediate(file, repository) ? ".NET obj intermediates" : IsVisualStudioIndex(file, repository) ? "Visual Studio indexes" : buildOutput ? "MSBuild outputs" : file.Extension.ToLowerInvariant() switch
                        {
                            ".pch" or ".pchast" => "C++ precompiled headers",
                            ".ilk" => "C++ incremental linker cache",
                            ".pyc" => "Python bytecode",
                            _ => "C++ object files",
                        },
                        Action = "delete", Risk = RiskLevel.Medium,
                        ReasonEn = buildOutput ? "Git-ignored output listed by MSBuild, unchanged for at least 1 hour; rebuilding is required after cleanup" : "Git-ignored build cache with project or source evidence, unchanged for at least 1 hour",
                        ReasonZh = buildOutput ? "MSBuild 清单记录的 Git 忽略输出，至少 1 小时未修改；清理后需要重新构建" : "有项目或源码依据的 Git 忽略构建缓存，至少 1 小时未修改",
                    });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return items;
    }

    internal static bool IsEligible(FileInfo file, string root, string repository, string manifest = "") =>
        !string.IsNullOrEmpty(repository) &&
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(repository)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) &&
        IsRepository(repository) && (MatchesArtifact(file, repository) ||
            (!string.IsNullOrEmpty(manifest) && IsBuildOutput(file, repository) && ReadBuildManifest(manifest, repository).Contains(file.FullName, StringComparer.OrdinalIgnoreCase))) &&
        Git(repository, ["check-ignore", "--", Path.GetRelativePath(repository, file.FullName)], CancellationToken.None) != null;

    private static bool IsRepository(string path) =>
        OptimizationFileSafety.HasNoReparsePoints(path) && (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")));

    private static bool MatchesArtifact(FileInfo file, string repository)
    {
        string relative = Path.GetRelativePath(repository, file.FullName).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
        var parts = relative.Split('/');
        if (IsProtectedPath(parts)) return false;
        if (IsDotNetIntermediate(file, repository) || IsVisualStudioIndex(file, repository)) return true;
        if (file.Extension.Equals(".pyc", StringComparison.OrdinalIgnoreCase))
        {
            if (file.Directory?.Name != "__pycache__") return false;
            string stem = file.Name.Split('.')[0];
            return File.Exists(Path.Combine(file.Directory.Parent!.FullName, stem + ".py"));
        }
        if (!new[] { ".pch", ".pchast", ".obj", ".iobj", ".ilk" }.Contains(file.Extension, StringComparer.OrdinalIgnoreCase) ||
            !parts.Any(part => part.Equals("Debug", StringComparison.OrdinalIgnoreCase) || part.Equals("Release", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase))) return false;
        return Directory.EnumerateFiles(repository, "*.sln").Any() || Directory.EnumerateFiles(repository, "*.slnx").Any() || Directory.EnumerateFiles(repository, "*.vcxproj").Any();
    }

    private static bool IsProtectedPath(string[] parts) => parts.Any(part =>
        new[] { ".git", ".venv", "venv", "node_modules", "publish", "artifacts", "dist" }.Contains(part, StringComparer.OrdinalIgnoreCase));

    private static string? GetOutputDirectory(string path, string repository)
    {
        string relative = Path.GetRelativePath(repository, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;
        var parts = relative.Split(Path.DirectorySeparatorChar);
        string current = repository;
        foreach (string part in parts.Take(parts.Length - 1))
        {
            current = Path.Combine(current, part);
            if (part.Equals("Debug", StringComparison.OrdinalIgnoreCase) || part.Equals("Release", StringComparison.OrdinalIgnoreCase)) return current;
        }
        return null;
    }

    private static bool IsBuildOutput(FileInfo file, string repository)
    {
        string? output = GetOutputDirectory(file.FullName, repository);
        if (output == null || IsProtectedPath(Path.GetRelativePath(repository, file.FullName).Split(Path.DirectorySeparatorChar))) return false;
        // The optimizer must not remove its own running installation or its lazy-loaded dependencies.
        if (AppContext.BaseDirectory.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        return OutputExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase) ||
            file.Name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadBuildManifest(string path, string repository)
    {
        var results = new List<string>();
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(Path.GetFullPath(repository) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !fullPath.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase) || !OptimizationFileSafety.HasNoReparsePoints(fullPath)) return results;
            var manifest = new FileInfo(fullPath);
            if (!manifest.Exists || manifest.Length > 2 * 1024 * 1024) return results;
            string projectName = manifest.Name[..^ManifestSuffix.Length];
            if (!new[] { ".csproj", ".fsproj", ".vbproj", ".vcxproj" }.Contains(Path.GetExtension(projectName), StringComparer.OrdinalIgnoreCase)) return results;
            for (var directory = manifest.Directory; directory != null &&
                (directory.FullName.Equals(repository, StringComparison.OrdinalIgnoreCase) || directory.FullName.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); directory = directory.Parent)
            {
                string project = Path.Combine(directory.FullName, projectName);
                if (!File.Exists(project) || !OptimizationFileSafety.HasNoReparsePoints(project)) continue;
                foreach (string line in File.ReadLines(fullPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string candidate = Path.GetFullPath(line.Trim(), directory.FullName);
                    if (candidate.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) results.Add(candidate);
                }
                break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return results;
    }

    private static bool IsDotNetIntermediate(FileInfo file, string repository)
    {
        bool known = new[] { ".dll", ".pdb", ".cache" }.Contains(file.Extension, StringComparer.OrdinalIgnoreCase) ||
            file.Name.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) ||
            new[] { ".nuget.g.props", ".nuget.g.targets", ".AssemblyInfo.cs", ".GlobalUsings.g.cs", ".AssemblyAttributes.cs" }.Any(suffix => file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (!known) return false;
        for (var directory = file.Directory; directory != null && directory.FullName.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); directory = directory.Parent)
        {
            if (directory.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) && directory.Parent != null &&
                (directory.Parent.EnumerateFiles("*.csproj").Any() || directory.Parent.EnumerateFiles("*.fsproj").Any() || directory.Parent.EnumerateFiles("*.vbproj").Any())) return true;
        }
        return false;
    }

    private static bool IsVisualStudioIndex(FileInfo file, string repository) =>
        file.FullName.StartsWith(Path.Combine(repository, ".vs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        (file.Name.EndsWith(".VC.db", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".VC.opendb", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".ipch", StringComparison.OrdinalIgnoreCase));

    private static string? Git(string repository, IEnumerable<string> arguments, CancellationToken token)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--no-optional-locks");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("core.fsmonitor=false");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("core.untrackedCache=false");
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(token);
            var errors = process.StandardError.ReadToEndAsync(token);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                errors.GetAwaiter().GetResult();
                return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                token.ThrowIfCancellationRequested();
                return null;
            }
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (IOException) { return null; }
    }
}
