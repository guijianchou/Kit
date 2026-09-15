namespace Kit.AiHub.UnitTests;

using System;
using System.IO;

internal sealed class FixtureDirectory : IDisposable
{
    private readonly string _parentDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Kit.AiHub.Tests"));

    internal string RootPath { get; }

    internal FixtureDirectory()
    {
        RootPath = Path.Combine(_parentDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    internal string PathFor(params string[] components)
    {
        string candidate = Path.GetFullPath(Path.Combine(RootPath, Path.Combine(components)));
        if (!candidate.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The fixture path must stay inside its temporary directory.");
        }

        return candidate;
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(RootPath);
        if (!string.Equals(Path.GetDirectoryName(resolved), _parentDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
        {
            throw new InvalidOperationException("Refusing to remove an unexpected fixture directory.");
        }

        if (Directory.Exists(resolved))
        {
            // Directory.Delete removes directory links without traversing their targets.
            Directory.Delete(resolved, recursive: true);
        }
    }
}
