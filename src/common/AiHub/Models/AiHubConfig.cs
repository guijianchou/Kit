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
            MaxConcurrentAnalysis = 2
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
