using System;
using System.Collections.Generic;
using System.IO;

namespace Kit.AIHubLib.Services;

/// <summary>Host-owned exclusions. Model advice cannot make user state disposable.</summary>
internal static class OptimizationProtectionPolicy
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".rtf", ".odt", ".ods", ".odp", ".pages",
        ".numbers", ".pst", ".ost", ".mbox", ".eml", ".msg", ".one", ".vsdx", ".psd", ".ai", ".sketch", ".fig",
        ".blend", ".dwg", ".dxf", ".bak", ".backup",
    };

    private static readonly HashSet<string> StateExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".key", ".pem", ".pfx", ".p12", ".cer", ".crt", ".jks", ".kdbx", ".env", ".ini", ".cfg", ".conf",
        ".config", ".yaml", ".yml", ".toml",
    };

    private static readonly HashSet<string> StateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "credentials", "credentials.json", "auth.json", "tokens.json", "secrets.json", "settings.json", "config.json",
        "configuration.json", "id_rsa", "id_ed25519", "id_ecdsa", "authorized_keys", "known_hosts", "Cookies",
        "Login Data", "Web Data", "History", "Bookmarks", "Preferences", "Secure Preferences",
        ".npmrc", ".yarnrc", ".pypirc", ".netrc", "_netrc", ".gitconfig", ".git-credentials", ".dockercfg",
    };

    internal static bool IsProtectedFromCleanup(string path) =>
        IsSensitiveState(path) || DocumentExtensions.Contains(Path.GetExtension(path)) ||
        (HasTemporarySuffix(path) && DocumentExtensions.Contains(Path.GetExtension(Path.GetFileNameWithoutExtension(path))));

    internal static bool IsSensitiveState(string path)
    {
        string name = Path.GetFileName(path);
        if (HasTemporarySuffix(name) && IsSensitiveState(Path.GetFileNameWithoutExtension(name))) return true;
        if (StateExtensions.Contains(Path.GetExtension(name)) || StateNames.Contains(name) ||
            name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) return true;

        foreach (string segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals(".ssh", StringComparison.OrdinalIgnoreCase) || segment.Equals(".gnupg", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || segment.Equals(".aws", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".azure", StringComparison.OrdinalIgnoreCase) || segment.Equals(".kube", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Sessions", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool HasTemporarySuffix(string path) => Path.GetExtension(path).ToLowerInvariant() is ".tmp" or ".temp" or ".old" or ".bak";
}
