using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Kit.AIHubLib.Services;

/// <summary>File evidence required in a general-purpose temporary directory.</summary>
internal static class TemporaryCleanupPolicy
{
    private static readonly string[] ProjectNames = [".git", ".hg", ".svn", "CMakeLists.txt", "Cargo.toml", "package.json", "pyproject.toml", "go.mod"];
    private static readonly string[] ProjectPatterns = ["*.csproj", "*.vcxproj", "*.fsproj", "*.sln", "*.slnx"];
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern unsafe IntPtr FindFirstFileExW(string fileName, int infoLevel, void* findData, int searchOperation, IntPtr searchFilter, uint additionalFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    private static readonly HashSet<string> BuildExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".obj", ".o", ".iobj", ".pch", ".pchast", ".ipch", ".ilk", ".ipdb", ".pdb", ".tlog", ".pyc",
    };

    private static readonly HashSet<string> ResidualExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".dmp", ".mdmp", ".hdmp", ".etl", ".wer", ".cache",
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cpp", ".c", ".h", ".hpp", ".cxx", ".hxx", ".cc", ".py", ".pyw", ".js", ".jsx",
        ".ts", ".tsx", ".mjs", ".cjs", ".rs", ".go", ".java", ".kt", ".swift", ".rb", ".php",
        ".ps1", ".psm1", ".psd1", ".sh", ".bat", ".cmd", ".sql", ".csproj", ".vcxproj", ".sln", ".slnx",
    };

    internal static bool IsBuildArtifact(string path) => BuildExtensions.Contains(Path.GetExtension(path));

    internal static bool IsRecognizedFile(string path)
    {
        if (OptimizationProtectionPolicy.IsProtectedFromCleanup(path)) return false;
        string extension = Path.GetExtension(path);
        if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".temp", StringComparison.OrdinalIgnoreCase))
        {
            string original = Path.GetFileNameWithoutExtension(path);
            while (Path.GetExtension(original).ToLowerInvariant() is ".tmp" or ".temp") original = Path.GetFileNameWithoutExtension(original);
            return !SourceExtensions.Contains(Path.GetExtension(original)) && !OptimizationProtectionPolicy.IsProtectedFromCleanup(original);
        }

        return IsBuildArtifact(path) || ResidualExtensions.Contains(extension);
    }

    internal static bool IsProjectMarker(string name) => name.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".hg", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".svn", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)
        || name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
        || name.Equals("go.mod", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(name).ToLowerInvariant() is ".csproj" or ".vcxproj" or ".fsproj" or ".sln" or ".slnx";

    internal static bool HasNoProjectAncestor(string path, string root, CancellationToken cancellationToken)
    {
        for (string? current = Path.GetDirectoryName(path); current != null; current = Path.GetDirectoryName(current))
        {
            foreach (string name in ProjectNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string marker = Path.Combine(current, name);
                if (File.Exists(marker) || Directory.Exists(marker)) return false;
            }

            foreach (string pattern in ProjectPatterns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasProjectMatch(current, pattern)) return false;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static unsafe bool HasProjectMatch(string directory, string pattern)
    {
        // Ask Windows for the first matching project marker. Directory.EnumerateFiles
        // applies wildcards in managed code and would revisit every Temp entry per file.
        string query = Path.Combine(directory, pattern);
        if (query.Length >= 248 && !query.StartsWith(@"\\?\", StringComparison.Ordinal))
            query = query.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + query[2..] : @"\\?\" + query;
        byte* data = stackalloc byte[592]; // sizeof(WIN32_FIND_DATAW), including its fixed UTF-16 names.
        IntPtr handle = FindFirstFileExW(query, 1, data, 0, IntPtr.Zero, 0);
        if (handle == InvalidHandleValue)
        {
            // No match is safe. Access failures or a disappearing directory fail closed.
            return Marshal.GetLastWin32Error() != 2;
        }

        FindClose(handle);
        return true;
    }
}
