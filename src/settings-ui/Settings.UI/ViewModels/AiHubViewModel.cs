// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Security;
using Kit.AiHub.Storage;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.ViewModels;

/// <summary>
/// Model wrapper for individual endpoint route settings in the AI Hub UI.
/// </summary>
public sealed class AiHubEndpointViewModel : Observable
{
    public AiTargetSettings Settings { get; }

    public string Label { get; }

    public string Description { get; }

    public Visibility FallbackVisibility { get; }

    public ICommand TestCommand { get; }

    public string BaseUrl
    {
        get => Settings.BaseUrl;
        set
        {
            if (Settings.BaseUrl != value)
            {
                Settings.BaseUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public string ApiKey
    {
        get => Settings.ApiKey;
        set
        {
            if (Settings.ApiKey != value)
            {
                Settings.ApiKey = value;
                OnPropertyChanged();
            }
        }
    }

    public string Mode
    {
        get => Settings.Mode;
        set
        {
            if (Settings.Mode != value)
            {
                Settings.Mode = value;
                OnPropertyChanged();
            }
        }
    }

    public string Model
    {
        get => Settings.Model;
        set
        {
            if (Settings.Model != value)
            {
                Settings.Model = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelSummary));
            }
        }
    }

    public string Effort
    {
        get => Settings.Effort;
        set
        {
            if (Settings.Effort != value)
            {
                Settings.Effort = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelSummary));
            }
        }
    }

    public string ModelSummary => string.IsNullOrWhiteSpace(Settings.Model)
        ? "(Not configured)"
        : $"{Settings.Model} · {Settings.Effort}";

    public void NotifyUpdated()
    {
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(Effort));
        OnPropertyChanged(nameof(ModelSummary));
    }

    internal AiHubEndpointViewModel(AiTargetSettings settings, AiHubViewModel owner, bool fallback)
    {
        Settings = settings;
        Label = fallback ? "Fallback" : "Main";
        Description = fallback ? "Optional backup for eligible network and endpoint failures." : "Primary endpoint for new AI tasks.";
        FallbackVisibility = fallback ? Visibility.Visible : Visibility.Collapsed;
        TestCommand = new RelayCommand(() => owner.TestTarget(settings), () => owner.CanEdit);
    }
}

/// <summary>
/// ViewModel managing AI Hub runtime, endpoints, and security policies within Kit Settings.
/// </summary>
public sealed class AiHubViewModel : Observable, IDisposable
{
    private readonly AiHubSettingsStore _settingsStore = null!;
    private readonly SecurityPolicyService _securityService = null!;
    private readonly IAiTaskEngine? _engine;
    private readonly KernelManagerService _kernelManager = new();
    private readonly RouteDispatcher _routeDispatcher;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private AiHubConfig _config;
    private int _activeKernelIndex;
    private int _pendingKernelIndex;
    private bool _isBusy;
    private bool _disposed;
    private bool _loadFailed;
    private string _kernelStatusText = string.Empty;
    private string _kernelLatestText = string.Empty;
    private string _kernelPathText = string.Empty;
    private double _downloadProgress;
    private bool _showDownloadProgress;
    private string _securityPolicyContent = string.Empty;
    private string _securityPolicyStatusText = string.Empty;
    private string _securityAuditPolicyContent = string.Empty;
    private string _optimizationPolicyContent = string.Empty;
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity;
    private bool _isStatusOpen;

    public AiHubViewModel(DispatcherQueue? dispatcherQueue = null)
    {
        try
        {
            _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcherQueue = null;
        }

        _routeDispatcher = new RouteDispatcher(_kernelManager);
        try
        {
            _settingsStore = new AiHubSettingsStore();
            _securityService = new SecurityPolicyService();
            _config = _settingsStore.Load();
            _engine = AiHubEngine.Current;
        }
        catch (Exception)
        {
            _config = AiHubConfig.CreateDefault();
            _loadFailed = true;
        }

        _activeKernelIndex = AiKernelCatalog.IsPi(_config.SelectedKernel) ? 1 : 0;
        _pendingKernelIndex = _activeKernelIndex;
        ApplyKernelSwitchCommand = new RelayCommand(() => StartOperation(ApplyKernelSwitchAsync), () => CanEdit && IsKernelSwitchPending);
        CheckKernelUpdatesCommand = new RelayCommand(() => StartOperation(CheckKernelUpdatesAsync), () => CanEdit);
        DownloadKernelCommand = new RelayCommand(() => StartOperation(DownloadKernelAsync), () => CanEdit);
        SaveEndpointsCommand = new RelayCommand(SaveEndpoints, () => CanEdit);
        SaveCommand = new RelayCommand(() => StartOperation(SaveAllAsync), () => CanEdit);
        ClearFallbackCommand = new RelayCommand(ClearFallback, () => CanEdit);
        SavePolicyCommand = new RelayCommand(() => StartOperation(SavePolicyAsync), () => CanEdit && !string.IsNullOrWhiteSpace(SecurityPolicyContent));
        ResetPolicyCommand = new RelayCommand(() => StartOperation(ResetPolicyAsync), () => CanEdit);
        SaveSecurityAuditPolicyCommand = new RelayCommand(() => StartOperation(SaveSecurityAuditPolicyAsync), () => CanEdit && !string.IsNullOrWhiteSpace(SecurityAuditPolicyContent));
        ResetSecurityAuditPolicyCommand = new RelayCommand(() => StartOperation(ResetSecurityAuditPolicyAsync), () => CanEdit);
        SaveOptimizationPolicyCommand = new RelayCommand(() => StartOperation(SaveOptimizationPolicyAsync), () => CanEdit && !string.IsNullOrWhiteSpace(OptimizationPolicyContent));
        ResetOptimizationPolicyCommand = new RelayCommand(() => StartOperation(ResetOptimizationPolicyAsync), () => CanEdit);
        SelfTestCommand = new RelayCommand(
            () => StartOperation(RunSelfTestAsync),
            () => CanRunSelfTest && IsEnabled);

        SaveActivePolicyCommand = new RelayCommand(
            () =>
            {
                switch (ActivePolicyIndex)
                {
                    case 1:
                        StartOperation(SaveSecurityAuditPolicyAsync);
                        break;
                    case 2:
                        StartOperation(SaveOptimizationPolicyAsync);
                        break;
                    default:
                        StartOperation(SavePolicyAsync);
                        break;
                }
            },
            () => CanEdit && !string.IsNullOrWhiteSpace(CurrentPolicyContent));
        ResetActivePolicyCommand = new RelayCommand(
            () =>
            {
                switch (ActivePolicyIndex)
                {
                    case 1:
                        StartOperation(ResetSecurityAuditPolicyAsync);
                        break;
                    case 2:
                        StartOperation(ResetOptimizationPolicyAsync);
                        break;
                    default:
                        StartOperation(ResetPolicyAsync);
                        break;
                }
            },
            () => CanEdit);
        CancelOperationCommand = new RelayCommand(() => _operation?.Cancel(), () => IsBusy);
        BuildEndpoints();
        if (_engine is not null)
        {
            _engine.StateChanged += OnHubStateChanged;
        }

        RefreshLocalKernelStatus();
        _ = InitializeAsync();
    }

    public bool CanToggle => !_loadFailed && !_disposed;

    public bool CanEdit => IsEnabled && !IsBusy && CanToggle;

    public bool IsEnabled
    {
        get => _config.IsEnabled;
        set
        {
            if (value == IsEnabled || !CanToggle)
            {
                return;
            }

            try
            {
                _settingsStore.Update(config => config.IsEnabled = value);
                _config.IsEnabled = value;
                if (!value)
                {
                    _operation?.Cancel();
                    IsStatusOpen = false;
                }

                NotifyEnabledChanged();
                AiHubEngine.RaiseStateChanged();
                if (value)
                {
                    _ = InitializeAsync();
                }
            }
            catch (Exception)
            {
                OnPropertyChanged();
                ShowStatus("Save failed.", InfoBarSeverity.Error);
            }
        }
    }

    public Visibility HubContentVisibility => IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    public string ActiveKernelName => AiKernelCatalog.GetDisplayName(_config.SelectedKernel);

    private string PendingKernel => PendingKernelIndex == 1 ? AiKernelCatalog.Pi : AiKernelCatalog.Codex;

    public int PendingKernelIndex
    {
        get => _pendingKernelIndex;
        set
        {
            if (value is < 0 or > 1 || !Set(ref _pendingKernelIndex, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsKernelSwitchPending));
            KernelLatestText = string.Empty;
            RefreshLocalKernelStatus();
            RefreshCommands();
        }
    }

    public bool IsKernelSwitchPending => _pendingKernelIndex != _activeKernelIndex;

    public double MaxConcurrentAnalysis
    {
        get => _config.MaxConcurrentAnalysis;
        set
        {
            if (!CanEdit || double.IsNaN(value))
            {
                return;
            }

            int limit = Math.Clamp((int)value, 1, 4);
            try
            {
                _settingsStore.Update(config => config.MaxConcurrentAnalysis = limit);
                _config.MaxConcurrentAnalysis = limit;
                OnPropertyChanged();
                AiHubEngine.RaiseStateChanged();
            }
            catch (Exception)
            {
                ShowStatus("Save failed.", InfoBarSeverity.Error);
            }
        }
    }

    public string KernelStatusText { get => _kernelStatusText; private set => Set(ref _kernelStatusText, value); }

    public string KernelLatestText { get => _kernelLatestText; private set => Set(ref _kernelLatestText, value); }

    public string KernelPathText { get => _kernelPathText; private set => Set(ref _kernelPathText, value); }

    public double DownloadProgress { get => _downloadProgress; private set => Set(ref _downloadProgress, value); }

    public bool ShowDownloadProgress { get => _showDownloadProgress; private set => Set(ref _showDownloadProgress, value); }

    public string SecurityPolicyPath => _securityService?.GlobalSecurityPolicyPath ?? string.Empty;

    /// <summary>Matches the UI language used across this view model.</summary>
    private static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private int _activePolicyIndex;
    private string _selfTestStatusText = string.Empty;
    private bool _isSelfTesting;

    public int ActivePolicyIndex
    {
        get => _activePolicyIndex;
        set
        {
            if (Set(ref _activePolicyIndex, value))
            {
                OnPropertyChanged(nameof(ActivePolicyTabIndex));
                NotifyTaskPolicyIndexChanged();
                OnPropertyChanged(nameof(CurrentPolicyContent));
                OnPropertyChanged(nameof(CurrentPolicyFilePath));
                OnPropertyChanged(nameof(CurrentPolicyDescription));
                ((RelayCommand)SaveActivePolicyCommand)?.OnCanExecuteChanged();
                ((RelayCommand)ResetActivePolicyCommand)?.OnCanExecuteChanged();
            }
        }
    }

    public int ActivePolicyTabIndex
    {
        get => ActivePolicyIndex;
        set => ActivePolicyIndex = value;
    }

    /// <summary>
    /// Policy selected on the AI Hub page, which now offers only the task-specific chains:
    /// 0 = security audit, 1 = optimization.
    /// </summary>
    /// <remarks>
    /// The global security policy moved to General because it governs every plugin, so this
    /// index is offset by one into the underlying set. Keeping the offset here means the
    /// stored content, file path and reset/save commands need no change.
    /// </remarks>
    public int TaskPolicyIndex
    {
        get => Math.Max(0, ActivePolicyIndex - 1);
        set => ActivePolicyIndex = Math.Max(0, value) + 1;
    }

    /// <summary>Raises change notifications for the task-scoped policy selector.</summary>
    private void NotifyTaskPolicyIndexChanged()
    {
        OnPropertyChanged(nameof(TaskPolicyIndex));
        OnPropertyChanged(nameof(IsSecurityAuditPolicyTab));
        OnPropertyChanged(nameof(IsOptimizationPolicyTab));
    }

    /// <summary>True when the security-audit chain policy is selected.</summary>
    public bool IsSecurityAuditPolicyTab => TaskPolicyIndex == 0;

    /// <summary>True when the optimization chain policy is selected.</summary>
    public bool IsOptimizationPolicyTab => TaskPolicyIndex == 1;

    public string CurrentPolicyContent
    {
        get => ActivePolicyIndex switch
        {
            1 => SecurityAuditPolicyContent,
            2 => OptimizationPolicyContent,
            _ => SecurityPolicyContent,
        };
        set
        {
            switch (ActivePolicyIndex)
            {
                case 1:
                    SecurityAuditPolicyContent = value;
                    break;
                case 2:
                    OptimizationPolicyContent = value;
                    break;
                default:
                    SecurityPolicyContent = value;
                    break;
            }

            OnPropertyChanged();
            ((RelayCommand)SaveActivePolicyCommand)?.OnCanExecuteChanged();
        }
    }

    public string SecurityPolicyStatusText { get => _securityPolicyStatusText; private set => Set(ref _securityPolicyStatusText, value); }

    public string SecurityAuditPolicyPath => _securityService?.GetTaskPolicyPath("security-audit") ?? string.Empty;

    public string OptimizationPolicyPath => _securityService?.GetTaskPolicyPath("system-optimization") ?? string.Empty;

    public string CurrentPolicyFilePath => ActivePolicyIndex switch
    {
        1 => SecurityAuditPolicyPath,
        2 => OptimizationPolicyPath,
        _ => SecurityPolicyPath,
    };

    public string CurrentPolicyDescription => ActivePolicyIndex switch
    {
        1 => "Task-specific policy and security constraints for security scanning agents.",
        2 => "Task-specific guidelines and safety rules for system optimization agents.",
        _ => "Global safety rules and constraints applied to all AI executions and agents.",
    };

    public string SecurityAuditPolicyContent
    {
        get => _securityAuditPolicyContent;
        set
        {
            if (Set(ref _securityAuditPolicyContent, value))
            {
                ((RelayCommand)SaveSecurityAuditPolicyCommand)?.OnCanExecuteChanged();
                if (ActivePolicyIndex == 1)
                {
                    OnPropertyChanged(nameof(CurrentPolicyContent));
                    ((RelayCommand)SaveActivePolicyCommand)?.OnCanExecuteChanged();
                }
            }
        }
    }

    public string OptimizationPolicyContent
    {
        get => _optimizationPolicyContent;
        set
        {
            if (Set(ref _optimizationPolicyContent, value))
            {
                ((RelayCommand)SaveOptimizationPolicyCommand)?.OnCanExecuteChanged();
                if (ActivePolicyIndex == 2)
                {
                    OnPropertyChanged(nameof(CurrentPolicyContent));
                    ((RelayCommand)SaveActivePolicyCommand)?.OnCanExecuteChanged();
                }
            }
        }
    }

    public string SecurityPolicyContent
    {
        get => _securityPolicyContent;
        set
        {
            if (Set(ref _securityPolicyContent, value))
            {
                SecurityPolicyStatusText = "Draft";
                ((RelayCommand)SavePolicyCommand).OnCanExecuteChanged();
                if (ActivePolicyIndex == 0)
                {
                    OnPropertyChanged(nameof(CurrentPolicyContent));
                    ((RelayCommand)SaveActivePolicyCommand)?.OnCanExecuteChanged();
                }
            }
        }
    }

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }

    public bool IsStatusOpen { get => _isStatusOpen; set => Set(ref _isStatusOpen, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                RefreshCommands();
            }
        }
    }

    public ObservableCollection<AiHubEndpointViewModel> Endpoints { get; } = new();

    public AiHubEndpointViewModel? MainEndpoint => Endpoints.Count > 0 ? Endpoints[0] : null;

    public AiHubEndpointViewModel? FallbackEndpoint => Endpoints.Count > 1 ? Endpoints[1] : null;

    public ICommand ApplyKernelSwitchCommand { get; }

    public ICommand CheckKernelUpdatesCommand { get; }

    public ICommand DownloadKernelCommand { get; }

    public ICommand SaveEndpointsCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand ClearFallbackCommand { get; }

    public ICommand SavePolicyCommand { get; }

    public ICommand ResetPolicyCommand { get; }

    public ICommand SaveSecurityAuditPolicyCommand { get; }

    public ICommand ResetSecurityAuditPolicyCommand { get; }

    public ICommand SaveOptimizationPolicyCommand { get; }

    public ICommand ResetOptimizationPolicyCommand { get; }

    /// <summary>
    /// Runs the built-in service self-test and reports the outcome.
    /// </summary>
    /// <remarks>
    /// Gives the operator a way to attribute a failure: if the self-test passes, the service
    /// is sound and a plugin issue lies elsewhere.
    /// </remarks>
    public ICommand SelfTestCommand { get; }

    /// <summary>
    /// Runs the built-in probe and surfaces a one-line outcome.
    /// </summary>
    private async Task RunSelfTestAsync(CancellationToken cancellationToken)
    {
        if (!CanRunSelfTest)
        {
            return;
        }

        IsSelfTesting = true;
        SelfTestStatusText = IsChinese ? "正在运行 AI 服务自检..." : "Running the AI service self-test...";

        try
        {
            SelfTestResult outcome = await AiServiceSelfTest
                .RunAsync(engine: null, cancellationToken)
                .ConfigureAwait(true);

            SelfTestStatusText = outcome.Success
                ? (IsChinese
                    ? $"自检通过（{outcome.Elapsed.TotalMilliseconds:F0} ms，经由 {outcome.UsedRoute}，模型 {outcome.UsedModel}）"
                    : $"Self-test passed ({outcome.Elapsed.TotalMilliseconds:F0} ms via {outcome.UsedRoute}, model {outcome.UsedModel})")
                : (IsChinese
                    ? $"自检失败：{outcome.ErrorMessage}"
                    : $"Self-test failed: {outcome.ErrorMessage}");

            Logger.LogInfo($"AI service self-test: {outcome.Summary}");
        }
        catch (Exception ex)
        {
            SelfTestStatusText = IsChinese ? $"自检异常：{ex.Message}" : $"Self-test error: {ex.Message}";
            Logger.LogError("AI service self-test threw", ex);
        }
        finally
        {
            IsSelfTesting = false;
        }
    }

    /// <summary>Result of the most recent self-test, or an empty string.</summary>
    public string SelfTestStatusText
    {
        get => _selfTestStatusText;
        private set => Set(ref _selfTestStatusText, value);
    }

    /// <summary>True while a self-test is running.</summary>
    public bool IsSelfTesting
    {
        get => _isSelfTesting;
        private set
        {
            if (Set(ref _isSelfTesting, value))
            {
                OnPropertyChanged(nameof(CanRunSelfTest));
            }
        }
    }

    /// <summary>Gate for the self-test action.</summary>
    public bool CanRunSelfTest => !IsSelfTesting && !_disposed;

    public ICommand SaveActivePolicyCommand { get; }

    public ICommand ResetActivePolicyCommand { get; }

    public ICommand CancelOperationCommand { get; }

    public async Task InitializeAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        if (_loadFailed)
        {
            ShowStatus("Failed to load AI Hub settings.", InfoBarSeverity.Error);
            return;
        }

        await RunOperationAsync(async token =>
        {
            RefreshLocalKernelStatus();
            SecurityPolicyContent = await _securityService.LoadGlobalPolicyAsync(token);
            if (string.IsNullOrWhiteSpace(SecurityPolicyContent))
            {
                SecurityPolicyContent = SecurityPolicyService.GetDefaultPolicyContent();
            }

            SecurityAuditPolicyContent = await _securityService.LoadTaskAgentsPolicyAsync("aihub", "security-audit", token);
            if (string.IsNullOrWhiteSpace(SecurityAuditPolicyContent))
            {
                SecurityAuditPolicyContent = TaskPolicyDefaults.DefaultSecurityAuditInstructions;
            }

            OptimizationPolicyContent = await _securityService.LoadTaskAgentsPolicyAsync("aihub", "system-optimization", token);
            if (string.IsNullOrWhiteSpace(OptimizationPolicyContent))
            {
                OptimizationPolicyContent = TaskPolicyDefaults.DefaultSystemOptimizationInstructions;
            }

            SecurityPolicyStatusText = "Active";
        });
    }

    internal void TestTarget(AiTargetSettings target) => StartOperation(async token =>
    {
        if (!RouteDispatcher.IsTargetCompatible(_config.SelectedKernel, target))
        {
            ShowStatus("Endpoint configuration is not compatible with the selected kernel.", InfoBarSeverity.Warning);
            return;
        }

        ShowStatus($"Testing connection to {target.Name}...", InfoBarSeverity.Informational);
        var result = await _routeDispatcher.TestConnectionAsync(target.Clone(), _config.SelectedKernel, token);
        ShowStatus(result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    });

    private async Task ApplyKernelSwitchAsync(CancellationToken token)
    {
        string candidate = PendingKernel;
        AiKernelStatus status = await _kernelManager.GetStatusAsync(candidate, token);
        if (!status.Installed || string.IsNullOrWhiteSpace(status.Version))
        {
            ShowStatus($"Please download and verify {AiKernelCatalog.GetDisplayName(candidate)} before applying.", InfoBarSeverity.Warning);
            return;
        }

        var saved = _settingsStore.Load();
        if (saved.Targets.Any(target => target.IsActive && !string.IsNullOrWhiteSpace(target.BaseUrl) && !RouteDispatcher.IsTargetCompatible(candidate, target)))
        {
            ShowStatus("Persisted endpoints are incompatible with the selected kernel.", InfoBarSeverity.Warning);
            return;
        }

        _settingsStore.Update(config => config.SelectedKernel = candidate);
        _config.SelectedKernel = candidate;
        _activeKernelIndex = PendingKernelIndex;
        OnPropertyChanged(nameof(IsKernelSwitchPending));
        OnPropertyChanged(nameof(ActiveKernelName));
        AiHubEngine.RaiseStateChanged();
        RefreshLocalKernelStatus();
        ShowStatus($"Switched active kernel to {AiKernelCatalog.GetDisplayName(candidate)}.", InfoBarSeverity.Success);
    }

    private async Task CheckKernelUpdatesAsync(CancellationToken token)
    {
        KernelLatestText = "Checking latest release...";
        string candidate = PendingKernel;
        string latest = await _kernelManager.CheckLatestVersionAsync(candidate, token);
        AiKernelStatus current = await _kernelManager.GetStatusAsync(candidate, token);
        KernelLatestText = !current.Installed
            ? $"Latest available: {latest}"
            : KernelManagerService.CompareVersions(current.Version, latest) < 0 ? $"Update available: {latest}" : $"Up to date ({latest})";
        RefreshLocalKernelStatus();
    }

    private async Task DownloadKernelAsync(CancellationToken token)
    {
        ShowDownloadProgress = true;
        DownloadProgress = 0;
        ShowStatus($"Downloading {AiKernelCatalog.GetDisplayName(PendingKernel)} kernel...", InfoBarSeverity.Informational);
        var progress = new Progress<double>(value =>
        {
            if (!_disposed)
            {
                DownloadProgress = value;
            }
        });
        var result = await _kernelManager.DownloadOrUpdateAsync(PendingKernel, progress, token);
        RefreshLocalKernelStatus();
        KernelLatestText = $"Latest: {result.LatestVersion}";
        ShowStatus(result.Changed ? $"{AiKernelCatalog.GetDisplayName(PendingKernel)} installed successfully." : $"{AiKernelCatalog.GetDisplayName(PendingKernel)} is already up to date.", InfoBarSeverity.Success);
    }

    private void SaveEndpoints()
    {
        try
        {
            var main = _config.GetMainTarget();
            if (main is null || !RouteDispatcher.IsTargetCompatible(_config.SelectedKernel, main)
                || _config.Targets.Skip(1).Any(target => target.IsActive && !RouteDispatcher.IsTargetCompatible(_config.SelectedKernel, target)))
            {
                ShowStatus("One or more endpoint configurations are invalid or incompatible.", InfoBarSeverity.Warning);
                return;
            }

            _settingsStore.Update(config => config.Targets = new ObservableCollection<AiTargetSettings>(_config.Targets.Select(target => target.Clone())));
            AiHubEngine.RaiseStateChanged();
            ShowStatus("Endpoints saved successfully.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to save endpoints: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task SaveAllAsync(CancellationToken token)
    {
        var main = _config.GetMainTarget();
        if (main is null || !RouteDispatcher.IsTargetCompatible(_config.SelectedKernel, main)
            || _config.Targets.Skip(1).Any(target => target.IsActive && !RouteDispatcher.IsTargetCompatible(_config.SelectedKernel, target)))
        {
            ShowStatus("One or more endpoint configurations are invalid or incompatible.", InfoBarSeverity.Warning);
            return;
        }

        _settingsStore.Update(config => config.Targets = new ObservableCollection<AiTargetSettings>(_config.Targets.Select(target => target.Clone())));
        AiHubEngine.RaiseStateChanged();

        if (!string.IsNullOrWhiteSpace(SecurityPolicyContent))
        {
            await _securityService.SaveGlobalPolicyAsync(SecurityPolicyContent, token);
            SecurityPolicyStatusText = "Saved";
        }

        if (!string.IsNullOrWhiteSpace(SecurityAuditPolicyContent))
        {
            await _securityService.SaveTaskPolicyAsync("security-audit", SecurityAuditPolicyContent, token);
        }

        if (!string.IsNullOrWhiteSpace(OptimizationPolicyContent))
        {
            await _securityService.SaveTaskPolicyAsync("system-optimization", OptimizationPolicyContent, token);
        }

        ShowStatus("Settings saved successfully.", InfoBarSeverity.Success);
    }

    private void ClearFallback()
    {
        if (_config.GetFallbackTarget() is { } target)
        {
            target.IsActive = false;
            target.BaseUrl = string.Empty;
            target.ApiKey = string.Empty;
            FallbackEndpoint?.NotifyUpdated();
            ShowStatus("Fallback endpoint cleared.", InfoBarSeverity.Informational);
        }
    }

    private async Task SavePolicyAsync(CancellationToken token)
    {
        await _securityService.SaveGlobalPolicyAsync(SecurityPolicyContent, token);
        if (!string.IsNullOrWhiteSpace(SecurityAuditPolicyContent))
        {
            await _securityService.SaveTaskPolicyAsync("security-audit", SecurityAuditPolicyContent, token);
        }

        if (!string.IsNullOrWhiteSpace(OptimizationPolicyContent))
        {
            await _securityService.SaveTaskPolicyAsync("system-optimization", OptimizationPolicyContent, token);
        }

        SecurityPolicyStatusText = "Saved";
        ShowStatus("Security policy saved successfully.", InfoBarSeverity.Success);
    }

    private async Task ResetPolicyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _securityService.ResetGlobalPolicy();
        SecurityPolicyContent = await _securityService.LoadGlobalPolicyAsync(token);
        SecurityPolicyStatusText = "Default";
        ShowStatus("Security policy reset to default template.", InfoBarSeverity.Success);
    }

    private async Task ResetSecurityAuditPolicyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _securityService.ResetTaskPolicy("security-audit");
        SecurityAuditPolicyContent = await _securityService.LoadTaskAgentsPolicyAsync("aihub", "security-audit", token);
        ShowStatus("Security audit policy reset to default template.", InfoBarSeverity.Success);
    }

    private async Task ResetOptimizationPolicyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _securityService.ResetTaskPolicy("system-optimization");
        OptimizationPolicyContent = await _securityService.LoadTaskAgentsPolicyAsync("aihub", "system-optimization", token);
        ShowStatus("Optimization policy reset to default template.", InfoBarSeverity.Success);
    }

    private async Task SaveSecurityAuditPolicyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(SecurityAuditPolicyContent))
        {
            await _securityService.SaveTaskPolicyAsync("security-audit", SecurityAuditPolicyContent, token);
            ShowStatus("Security audit policy (AGENTS.md) saved successfully.", InfoBarSeverity.Success);
        }
    }

    private async Task SaveOptimizationPolicyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(OptimizationPolicyContent))
        {
            await _securityService.SaveTaskPolicyAsync("system-optimization", OptimizationPolicyContent, token);
            ShowStatus("Optimization policy (AGENTS.md) saved successfully.", InfoBarSeverity.Success);
        }
    }

    private void RefreshLocalKernelStatus()
    {
        var status = _kernelManager.GetStatus(PendingKernel);
        KernelStatusText = status.Installed
            ? $"{AiKernelCatalog.GetDisplayName(PendingKernel)}: Installed ({(string.IsNullOrEmpty(status.Version) ? "unknown version" : status.Version)})"
            : $"{AiKernelCatalog.GetDisplayName(PendingKernel)}: Not installed";
        KernelPathText = status.Path;
    }

    private void BuildEndpoints()
    {
        Endpoints.Clear();
        for (int index = 0; index < _config.Targets.Count; index++)
        {
            Endpoints.Add(new AiHubEndpointViewModel(_config.Targets[index], this, index == 1));
        }

        OnPropertyChanged(nameof(MainEndpoint));
        OnPropertyChanged(nameof(FallbackEndpoint));
    }

    private void StartOperation(Func<CancellationToken, Task> operation)
    {
        if (CanEdit)
        {
            _ = RunOperationAsync(operation);
        }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = cancellation;
        IsBusy = true;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsEnabled)
            {
                ShowStatus("Operation cancelled.", InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Operation failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _operation = null;
            ShowDownloadProgress = false;
            IsBusy = false;
        }
    }

    private void OnHubStateChanged(object? sender, AiHubStateChangedEventArgs args)
    {
        void UpdateState()
        {
            if (_disposed)
            {
                return;
            }

            if (_config.SelectedKernel != args.ActiveKernel)
            {
                bool hadPendingSelection = IsKernelSwitchPending;
                _config.SelectedKernel = args.ActiveKernel;
                _activeKernelIndex = AiKernelCatalog.IsPi(args.ActiveKernel) ? 1 : 0;
                if (!hadPendingSelection)
                {
                    _pendingKernelIndex = _activeKernelIndex;
                    OnPropertyChanged(nameof(PendingKernelIndex));
                }

                OnPropertyChanged(nameof(IsKernelSwitchPending));
                OnPropertyChanged(nameof(ActiveKernelName));
                RefreshLocalKernelStatus();
            }

            bool enabledChanged = _config.IsEnabled != args.IsEnabled;
            _config.IsEnabled = args.IsEnabled;
            if (!args.IsEnabled)
            {
                _operation?.Cancel();
                IsStatusOpen = false;
            }

            NotifyEnabledChanged();
            if (enabledChanged && args.IsEnabled)
            {
                _ = InitializeAsync();
            }
        }

        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(UpdateState);
        }
        else
        {
            UpdateState();
        }
    }

    private void NotifyEnabledChanged()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(HubContentVisibility));
        OnPropertyChanged(nameof(CanEdit));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        foreach (var command in new[] { ApplyKernelSwitchCommand, CheckKernelUpdatesCommand, DownloadKernelCommand, SaveCommand, SaveEndpointsCommand, ClearFallbackCommand, SavePolicyCommand, ResetPolicyCommand, SaveSecurityAuditPolicyCommand, ResetSecurityAuditPolicyCommand, SaveOptimizationPolicyCommand, ResetOptimizationPolicyCommand, SaveActivePolicyCommand, ResetActivePolicyCommand, CancelOperationCommand })
        {
            ((RelayCommand)command).OnCanExecuteChanged();
        }

        foreach (var endpoint in Endpoints)
        {
            ((RelayCommand)endpoint.TestCommand).OnCanExecuteChanged();
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (!_disposed)
        {
            StatusMessage = message;
            StatusSeverity = severity;
            IsStatusOpen = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_engine is not null)
        {
            _engine.StateChanged -= OnHubStateChanged;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
