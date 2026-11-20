using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kit.Updater;

// Values 0-4 are shared with the runner and Settings.UI.Library. Append only.
internal enum UpdateStatus
{
    UpToDate,
    ErrorDownloading,
    ReadyToDownload,
    ReadyToInstall,
    NetworkError,
    Downloading,
    Verifying,
    Installing,
}

internal sealed class UpdateState
{
    public UpdateStatus State { get; set; }
    public string ReleasePageUrl { get; set; } = string.Empty;
    public string GithubUpdateLastCheckedDate { get; set; } = string.Empty;
    public string DownloadedInstallerFilename { get; set; } = string.Empty;
    public string UpdateStateFileVersion { get; set; } = string.Empty;
    public string InstallRoot { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string PackageSha256 { get; set; } = string.Empty;
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
}

internal sealed record ReleaseInfo(Version Version, string Page, string Name, Uri Download, Uri Checksum, long Size, string Digest);

internal sealed class PortableManifest
{
    public string Version { get; set; } = string.Empty;
    public List<ManifestFile> Files { get; set; } = [];
}

internal sealed class ManifestFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class TransactionJournal
{
    public string InstallRoot { get; set; } = string.Empty;
    public string Phase { get; set; } = "prepared";
    public List<JournalFile> Files { get; set; } = [];
}

internal sealed class JournalFile
{
    public string Path { get; set; } = string.Empty;
    public bool Existed { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(UpdateState))]
[JsonSerializable(typeof(PortableManifest))]
[JsonSerializable(typeof(TransactionJournal))]
internal sealed partial class UpdateJson : JsonSerializerContext;

internal sealed class UpdateException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
