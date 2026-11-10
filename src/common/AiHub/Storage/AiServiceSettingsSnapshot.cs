namespace Kit.AiHub.Storage;

using Kit.AiHub.Models;

/// <summary>Public service preferences. Endpoint profiles live only in the DPAPI store.</summary>
public sealed class AiServiceSettingsSnapshot
{
    public int TargetStorageVersion { get; set; }
    public string TargetStorageId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string SelectedKernel { get; set; } = AiKernelCatalog.Codex;
    public int MaxConcurrentAnalysis { get; set; } = 2;
    public int ScanIntervalHours { get; set; }
    public int AuditModeIndex { get; set; }
    public int RetentionDays { get; set; } = 30;

    public static AiServiceSettingsSnapshot FromConfig(AiHubConfig config, string storageId) => new()
    {
        TargetStorageVersion = 1,
        TargetStorageId = storageId,
        IsEnabled = config.IsEnabled,
        SelectedKernel = config.SelectedKernel,
        MaxConcurrentAnalysis = config.MaxConcurrentAnalysis,
        ScanIntervalHours = config.ScanIntervalHours,
        AuditModeIndex = config.AuditModeIndex,
        RetentionDays = config.RetentionDays,
    };

    public AiHubConfig ToConfig() => new()
    {
        IsEnabled = IsEnabled,
        SelectedKernel = SelectedKernel,
        MaxConcurrentAnalysis = MaxConcurrentAnalysis,
        ScanIntervalHours = ScanIntervalHours,
        AuditModeIndex = AuditModeIndex,
        RetentionDays = RetentionDays,
    };
}
