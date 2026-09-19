namespace Kit.AiHub.Models;

using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Top-level configuration container for the AI Hub subsystem in Kit.
/// </summary>
public sealed partial class AiHubConfig : ObservableObject
{
    private bool _isEnabled;
    private string _selectedKernel = AiKernelCatalog.Codex;
    private int _maxConcurrentAnalysis = 2;
    private int _scanIntervalHours;
    private int _auditModeIndex;
    private int _retentionDays = 30;
    private ObservableCollection<AiTargetSettings> _targets = new();

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string SelectedKernel
    {
        get => _selectedKernel;
        set => SetProperty(ref _selectedKernel, value);
    }

    public int MaxConcurrentAnalysis
    {
        get => _maxConcurrentAnalysis;
        set => SetProperty(ref _maxConcurrentAnalysis, value);
    }

    /// <summary>
    /// Scheduled audit cadence in hours; 0 disables scheduling.
    /// </summary>
    /// <remarks>
    /// Lives here rather than only in the Settings model so a headless host can read the
    /// cadence without loading the UI settings assembly.
    /// </remarks>
    public int ScanIntervalHours
    {
        get => _scanIntervalHours;
        set => SetProperty(ref _scanIntervalHours, value);
    }

    /// <summary>Audit mode: 0 = extended (standard privileges), 1 = full (elevated).</summary>
    public int AuditModeIndex
    {
        get => _auditModeIndex;
        set => SetProperty(ref _auditModeIndex, value);
    }

    /// <summary>Audit history retention in days.</summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set => SetProperty(ref _retentionDays, value);
    }

    public ObservableCollection<AiTargetSettings> Targets
    {
        get => _targets;
        set => SetProperty(ref _targets, value);
    }

    public AiTargetSettings? GetMainTarget() =>
        Targets.Count > 0 ? Targets[0] : null;

    public AiTargetSettings? GetFallbackTarget() =>
        Targets.Count > 1 ? Targets[1] : null;

    /// <summary>
    /// Creates a default configuration with empty Main and Fallback targets.
    /// </summary>
    public static AiHubConfig CreateDefault()
    {
        var config = new AiHubConfig
        {
            IsEnabled = false,
            SelectedKernel = AiKernelCatalog.Codex,
            MaxConcurrentAnalysis = 2,
            ScanIntervalHours = 0,
            AuditModeIndex = 0,
            RetentionDays = 30,
        };

        config.Targets.Add(new AiTargetSettings
        {
            Name = "Main",
            IsActive = true,
            BaseUrl = string.Empty,
            ApiKey = string.Empty,
            Mode = "responses",
            Model = AiTargetSettings.DefaultLunaModel,
            Effort = "medium",
            IsExpanded = true
        });

        config.Targets.Add(new AiTargetSettings
        {
            Name = "Fallback",
            IsActive = false,
            BaseUrl = string.Empty,
            ApiKey = string.Empty,
            Mode = "responses",
            Model = AiTargetSettings.DefaultLunaModel,
            Effort = "medium",
            IsExpanded = false
        });

        return config;
    }
}
