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
    private double _contextWindowTokens;

    public AiTargetSettings Settings { get; }

    public string Label { get; }

    public string Description { get; }

    public Visibility FallbackVisibility { get; }

    public ICommand TestCommand { get; }

    public string[] ModeSuggestions { get; } = ["responses", "chat", "openai-responses", "openai-completions"];

    public string[] EffortSuggestions { get; } = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    public string BaseUrl
    {
        get => Settings.BaseUrl;
        set
        {
            if (Settings.BaseUrl != value)
            {
                Settings.BaseUrl = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EndpointSummary));
                OnEdited();
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
                OnEdited();
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
                OnEdited();
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
                OnEdited();
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
                OnEdited();
            }
        }
    }

    public double ContextWindowTokens
    {
        get => _contextWindowTokens;
        set
        {
            if (!_contextWindowTokens.Equals(value))
            {
                _contextWindowTokens = value;
                Settings.ContextWindowTokens = IsValidContextWindow(value) ? (int)value : 0;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ContextWindowError));
                OnPropertyChanged(nameof(ContextWindowErrorVisibility));
                OnEdited();
            }
        }
    }

    public string ContextWindowError => IsValidContextWindow(ContextWindowTokens)
        ? string.Empty
        : "AiHub_ContextWindowInvalid".GetLocalizedOrFallback("Enter a whole number of tokens between 1,024 and 2,147,483,647.");

    public Visibility ContextWindowErrorVisibility => IsValidContextWindow(ContextWindowTokens) ? Visibility.Collapsed : Visibility.Visible;

    private static bool IsValidContextWindow(double value) => double.IsFinite(value)
        && value >= AiTargetSettings.MinimumContextWindowTokens
        && value <= int.MaxValue
        && value == Math.Truncate(value);

    public string ModelSummary => string.IsNullOrWhiteSpace(Settings.Model)
        ? "AiHub_ModelNotSet".GetLocalizedOrFallback("No model set")
        : $"{Settings.Model} · {Settings.Effort}";

    /// <summary>Host of the configured URL, so a collapsed card still shows where it points.</summary>
    public string EndpointSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Settings.BaseUrl))
            {
                return "AiHub_EndpointNotConfigured".GetLocalizedOrFallback("Not configured");
            }

            return Uri.TryCreate(Settings.BaseUrl.Trim(), UriKind.Absolute, out Uri? uri) ? uri.Host : Settings.BaseUrl.Trim();
        }
    }

    private void OnEdited() => _owner.MarkEndpointsEdited();

    public void NotifyUpdated()
    {
        _contextWindowTokens = Settings.ContextWindowTokens;
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(Effort));
        OnPropertyChanged(nameof(ContextWindowTokens));
        OnPropertyChanged(nameof(ContextWindowError));
        OnPropertyChanged(nameof(ContextWindowErrorVisibility));
        OnPropertyChanged(nameof(ModelSummary));
        OnPropertyChanged(nameof(EndpointSummary));
    }

    private readonly AiHubViewModel _owner;

    internal AiHubEndpointViewModel(AiTargetSettings settings, AiHubViewModel owner, bool fallback)
    {
        Settings = settings;
        _contextWindowTokens = settings.ContextWindowTokens;
        _owner = owner;
        Label = fallback
            ? "AiHub_EndpointLabel_Fallback".GetLocalizedOrFallback("Fallback")
            : "AiHub_EndpointLabel_Main".GetLocalizedOrFallback("Main");
        Description = fallback
            ? "AiHub_EndpointDesc_Fallback".GetLocalizedOrFallback("Optional backup for eligible network and endpoint failures.")
            : "AiHub_EndpointDesc_Main".GetLocalizedOrFallback("Primary endpoint for new AI tasks.");
        FallbackVisibility = fallback ? Visibility.Visible : Visibility.Collapsed;
        TestCommand = new RelayCommand(() => owner.TestTarget(settings), () => owner.CanEdit);
    }
}

/// <summary>
/// ViewModel managing AI Hub runtime, endpoints, and security policies within Kit Settings.
/// </summary>
public sealed partial class AiHubViewModel : Observable, IDisposable
{
    private readonly KernelManagerService _kernelManager = new();
    private readonly RouteDispatcher _routeDispatcher;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PolicyDraft[] _policyDrafts = [new(), new(), new(), new()];
    private AiHubSettingsStore _settingsStore = null!;
    private SecurityPolicyService _securityService = null!;
    private IAiTaskEngine? _engine;
    private CancellationTokenSource? _operation;
    private bool _policyOperation;
    private AiHubConfig _config = AiHubConfig.CreateDefault();
    private int _activeKernelIndex;
    private int _pendingKernelIndex;
    private bool _isBusy;
    private bool _disposed;
    private bool _loadFailed;
    private bool _settingsLoaded;
    private string _kernelStatusText = string.Empty;
    private string _kernelLatestText = string.Empty;
    private string _kernelPathText = string.Empty;
    private double _downloadProgress;
    private bool _showDownloadProgress;
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity;
    private bool _isStatusOpen;
    private bool _hasUnsavedEndpointChanges;
    private string _serviceStatusText = string.Empty;
    private string _serviceStatusGlyph = "\uE9CE";

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
        _activeKernelIndex = AiKernelCatalog.IsPi(_config.SelectedKernel) ? 1 : 0;
        _pendingKernelIndex = _activeKernelIndex;
        ApplyKernelSwitchCommand = new RelayCommand(() => StartOperation(ApplyKernelSwitchAsync), () => CanEdit && IsKernelSwitchPending);
        CheckKernelUpdatesCommand = new RelayCommand(() => StartOperation(CheckKernelUpdatesAsync), () => CanEdit);
        DownloadKernelCommand = new RelayCommand(() => StartOperation(DownloadKernelAsync), () => CanEdit);
        SaveEndpointsCommand = new RelayCommand(SaveEndpoints, () => CanEdit && HasUnsavedEndpointChanges);
        DiscardEndpointChangesCommand = new RelayCommand(DiscardEndpointChanges, () => CanEdit && HasUnsavedEndpointChanges);
        SaveCommand = new RelayCommand(() => StartOperation(SaveAllAsync), () => CanEdit);
        ClearFallbackCommand = new RelayCommand(ClearFallback, () => CanEdit);
        SavePolicyCommand = new RelayCommand(() => StartPolicySave(0), () => CanSavePolicy(0));
        ResetPolicyCommand = new RelayCommand(() => StageDefaultPolicy(0), () => CanEditPolicies);
        SaveSecurityAuditPolicyCommand = new RelayCommand(() => StartPolicySave(1), () => CanSavePolicy(1));
        ResetSecurityAuditPolicyCommand = new RelayCommand(() => StageDefaultPolicy(1), () => CanEditPolicies);
        SaveOptimizationPolicyCommand = new RelayCommand(() => StartPolicySave(2), () => CanSavePolicy(2));
        ResetOptimizationPolicyCommand = new RelayCommand(() => StageDefaultPolicy(2), () => CanEditPolicies);
        SaveSelfTestPolicyCommand = new RelayCommand(() => StartPolicySave(3), () => CanSavePolicy(3));
        ResetSelfTestPolicyCommand = new RelayCommand(() => StageDefaultPolicy(3), () => CanEditPolicies);
        SaveActivePolicyCommand = new RelayCommand(() => StartPolicySave(ActivePolicyIndex), () => CanSavePolicy(ActivePolicyIndex));
        ResetActivePolicyCommand = new RelayCommand(() => StageDefaultPolicy(ActivePolicyIndex), () => CanEditPolicies);
        DiscardActivePolicyCommand = new RelayCommand(DiscardActivePolicy, () => CanEditPolicies && IsCurrentPolicyDirty);
        CancelOperationCommand = new RelayCommand(
            () =>
            {
                if (_settingsLoaded)
                {
                    _operation?.Cancel();
                }
            },
            () => _settingsLoaded && IsBusy);
        BuildEndpoints();
        Initialization = RunOperationAsync(LoadConfigurationAsync);
    }

    public Task Initialization { get; }

    private async Task LoadConfigurationAsync(CancellationToken token)
    {
        try
        {
            var loaded = await Task.Run(() =>
            {
                var settingsStore = new AiHubSettingsStore();
                var securityService = new SecurityPolicyService();
                var config = settingsStore.Load();
                var engine = AiHubEngine.Current;
                var status = _kernelManager.GetStatus(config.SelectedKernel);
                var readiness = engine.GetReadiness();
                return (settingsStore, securityService, config, engine, status, readiness);
            }, token);
            token.ThrowIfCancellationRequested();

            _settingsStore = loaded.settingsStore;
            _securityService = loaded.securityService;
            _config = loaded.config;
            _engine = loaded.engine;
            InitializeDiagnostics();
            _activeKernelIndex = AiKernelCatalog.IsPi(_config.SelectedKernel) ? 1 : 0;
            _pendingKernelIndex = _activeKernelIndex;
            _settingsLoaded = true;
            _engine.StateChanged += OnHubStateChanged;
            BuildEndpoints();
            OnPropertyChanged(nameof(PendingKernelIndex));
            OnPropertyChanged(nameof(IsKernelSwitchPending));
            OnPropertyChanged(nameof(ActiveKernelName));
            OnPropertyChanged(nameof(MaxConcurrentAnalysis));
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(SecurityPolicyPath));
            OnPropertyChanged(nameof(SecurityAuditPolicyPath));
            OnPropertyChanged(nameof(OptimizationPolicyPath));
            OnPropertyChanged(nameof(SelfTestPolicyPath));
            OnPropertyChanged(nameof(CurrentPolicyFilePath));
            NotifyEnabledChanged();
            ApplyLocalKernelStatus(_config.SelectedKernel, loaded.status);
            RefreshServiceStatus(loaded.readiness);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _loadFailed = true;
            ShowStatus(Text("AiHub_LoadFailed"), InfoBarSeverity.Error);
        }
    }

    public bool CanToggle => _settingsLoaded && !_loadFailed && !_disposed;

    public bool CanEdit => IsEnabled && !IsBusy && CanToggle;

    public bool CanEditPolicies => PoliciesLoaded && !IsBusy && CanToggle;

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
                    if (!_policyOperation)
                    {
                        _operation?.Cancel();
                    }
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
                ShowStatus(Text("AiHub_SaveFailed"), InfoBarSeverity.Error);
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
                ShowStatus(Text("AiHub_SaveFailed"), InfoBarSeverity.Error);
            }
        }
    }

    public string KernelStatusText { get => _kernelStatusText; private set => Set(ref _kernelStatusText, value); }

    public string KernelLatestText { get => _kernelLatestText; private set => Set(ref _kernelLatestText, value); }

    public string KernelPathText { get => _kernelPathText; private set => Set(ref _kernelPathText, value); }

    public double DownloadProgress { get => _downloadProgress; private set => Set(ref _downloadProgress, value); }

    public bool ShowDownloadProgress { get => _showDownloadProgress; private set => Set(ref _showDownloadProgress, value); }

    public string SecurityPolicyPath => _securityService?.GlobalSecurityPolicyPath ?? string.Empty;

    private int _activePolicyIndex;

    public int ActivePolicyIndex
    {
        get => _activePolicyIndex;
        set
        {
            if (value is >= 0 and <= 3 && Set(ref _activePolicyIndex, value))
            {
                OnPropertyChanged(nameof(ActivePolicyTabIndex));
                NotifyTaskPolicyIndexChanged();
                OnPropertyChanged(nameof(CurrentPolicyContent));
                OnPropertyChanged(nameof(CurrentPolicyFilePath));
                OnPropertyChanged(nameof(CurrentPolicyDescription));
                OnPropertyChanged(nameof(IsCurrentPolicyDirty));
                OnPropertyChanged(nameof(CurrentPolicyStatusText));
                ((RelayCommand)SaveActivePolicyCommand)?.OnCanExecuteChanged();
                ((RelayCommand)ResetActivePolicyCommand)?.OnCanExecuteChanged();
                ((RelayCommand)DiscardActivePolicyCommand)?.OnCanExecuteChanged();
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
    /// 0 = security audit, 1 = optimization, 2 = service self-test.
    /// </summary>
    /// <remarks>
    /// The global security policy moved to General because it governs every plugin, so this
    /// index is offset by one into the underlying set. Keeping the offset here means the
    /// stored content, file path and reset/save commands need no change.
    /// </remarks>
    public int TaskPolicyIndex
    {
        get => Math.Max(0, ActivePolicyIndex - 1);
        set
        {
            if (value is >= 0 and <= 2)
            {
                ActivePolicyIndex = value + 1;
            }
        }
    }

    /// <summary>Raises change notifications for the task-scoped policy selector.</summary>
    private void NotifyTaskPolicyIndexChanged()
    {
        OnPropertyChanged(nameof(TaskPolicyIndex));
        OnPropertyChanged(nameof(IsSecurityAuditPolicyTab));
        OnPropertyChanged(nameof(IsOptimizationPolicyTab));
        OnPropertyChanged(nameof(IsSelfTestPolicyTab));
    }

    /// <summary>True when the security-audit chain policy is selected.</summary>
    public bool IsSecurityAuditPolicyTab => TaskPolicyIndex == 0;

    /// <summary>True when the optimization chain policy is selected.</summary>
    public bool IsOptimizationPolicyTab => TaskPolicyIndex == 1;

    public bool IsSelfTestPolicyTab => TaskPolicyIndex == 2;

    private bool _policiesLoading;

    public bool PoliciesLoaded => _policyDrafts.All(draft => draft.IsLoaded);

    public bool IsCurrentPolicyDirty => _policyDrafts[ActivePolicyIndex].IsDirty;

    public bool HasUnsavedPolicyChanges => _policyDrafts.Any(draft => draft.IsDirty);

    public string CurrentPolicyStatusText => GetPolicyStatusText(ActivePolicyIndex);

    public void EnsurePoliciesLoaded()
    {
        if (PoliciesLoaded || _policiesLoading || _disposed || _loadFailed)
        {
            return;
        }

        _ = InitializeAsync();
    }

    public string CurrentPolicyContent
    {
        get
        {
            EnsurePoliciesLoaded();
            return _policyDrafts[ActivePolicyIndex].Content;
        }
        set
        {
            UpdatePolicyDraft(ActivePolicyIndex, value);
        }
    }

    public string SecurityPolicyStatusText => GetPolicyStatusText(0);

    public string SecurityAuditPolicyPath => _securityService?.GetTaskPolicyPath("security-audit") ?? string.Empty;

    public string OptimizationPolicyPath => _securityService?.GetTaskPolicyPath("system-optimization") ?? string.Empty;

    public string SelfTestPolicyPath => _securityService?.GetTaskPolicyPath(SelfTestPolicy.TaskId) ?? string.Empty;

    public string CurrentPolicyFilePath => ActivePolicyIndex switch
    {
        1 => SecurityAuditPolicyPath,
        2 => OptimizationPolicyPath,
        3 => SelfTestPolicyPath,
        _ => SecurityPolicyPath,
    };

    public string CurrentPolicyDescription => ActivePolicyIndex switch
    {
        1 => "AiHub_PolicyDescriptionAudit".GetLocalizedOrFallback("Review the supplied Windows events and distinguish evidence from possible explanations."),
        2 => "AiHub_PolicyDescriptionOptimization".GetLocalizedOrFallback("Review locally verified candidates without expanding their paths, actions or scope."),
        3 => "AiHub_PolicyDescriptionSelfTest".GetLocalizedOrFallback("A fixed ping probe checks the AI response contract without inspecting the workstation."),
        _ => "AiHub_PolicyDescriptionGlobal".GetLocalizedOrFallback("Shared safety and privacy rules apply to every AI task."),
    };

    public string SecurityAuditPolicyContent
    {
        get => _policyDrafts[1].Content;
        set => UpdatePolicyDraft(1, value);
    }

    public string OptimizationPolicyContent
    {
        get => _policyDrafts[2].Content;
        set => UpdatePolicyDraft(2, value);
    }

    public string SelfTestPolicyContent
    {
        get => _policyDrafts[3].Content;
        set => UpdatePolicyDraft(3, value);
    }

    public string SecurityPolicyContent
    {
        get => _policyDrafts[0].Content;
        set => UpdatePolicyDraft(0, value);
    }

    private void UpdatePolicyDraft(int index, string content)
    {
        if (_policyDrafts[index].SetContent(content))
        {
            NotifyPolicyDraftChanged(index);
        }
    }

    private void NotifyPolicyDraftChanged(int index)
    {
        OnPropertyChanged(index switch
        {
            1 => nameof(SecurityAuditPolicyContent),
            2 => nameof(OptimizationPolicyContent),
            3 => nameof(SelfTestPolicyContent),
            _ => nameof(SecurityPolicyContent),
        });
        OnPropertyChanged(nameof(SecurityPolicyStatusText));
        OnPropertyChanged(nameof(CurrentPolicyContent));
        OnPropertyChanged(nameof(IsCurrentPolicyDirty));
        OnPropertyChanged(nameof(HasUnsavedPolicyChanges));
        OnPropertyChanged(nameof(CurrentPolicyStatusText));
        OnPropertyChanged(nameof(PoliciesLoaded));
        OnPropertyChanged(nameof(CanEditPolicies));
        RefreshCommands();
    }

    private string GetPolicyStatusText(int index)
    {
        PolicyDraft draft = _policyDrafts[index];
        string state = !draft.IsLoaded
            ? "AiHub_PolicyStateLoading".GetLocalizedOrFallback("Loading policy")
            : draft.IsDirty
                ? "AiHub_PolicyStateDirty".GetLocalizedOrFallback("Unsaved draft")
                : "AiHub_PolicyStateSaved".GetLocalizedOrFallback("Saved");
        return $"{state} · {"AiHub_PolicyTemplateVersionFormat".FormatLocalized("Default template v{0}", SecurityPolicyService.CurrentTemplateVersion)}";
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
                OnPropertyChanged(nameof(CanEditPolicies));
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

    public ICommand DiscardEndpointChangesCommand { get; }

    /// <summary>
    /// True when an endpoint field differs from what is on disk. Edits stay in memory until
    /// saved, because the engine and other plugins read the persisted configuration only.
    /// </summary>
    public bool HasUnsavedEndpointChanges
    {
        get => _hasUnsavedEndpointChanges;
        private set
        {
            if (Set(ref _hasUnsavedEndpointChanges, value))
            {
                ((RelayCommand)SaveEndpointsCommand).OnCanExecuteChanged();
                ((RelayCommand)DiscardEndpointChangesCommand).OnCanExecuteChanged();
            }
        }
    }

    internal void MarkEndpointsEdited() => HasUnsavedEndpointChanges = true;

    /// <summary>One-line summary of the service state: enabled, kernel, model and route readiness.</summary>
    public string ServiceStatusText { get => _serviceStatusText; private set => Set(ref _serviceStatusText, value); }

    public string ServiceStatusGlyph { get => _serviceStatusGlyph; private set => Set(ref _serviceStatusGlyph, value); }

    /// <summary>Reads the cached readiness verdict. No probe or kernel process is started.</summary>
    private void RefreshServiceStatus(AiReadiness? cachedReadiness = null)
    {
        if (_engine is null)
        {
            ServiceStatusText = "AiHub_ServiceUnavailable".GetLocalizedOrFallback("AI service unavailable");
            ServiceStatusGlyph = "\uEA39";
            return;
        }

        try
        {
            AiReadiness readiness = cachedReadiness ?? _engine.GetReadiness();
            string kernelModel = string.IsNullOrWhiteSpace(readiness.ActiveModel)
                ? readiness.ActiveKernel
                : $"{readiness.ActiveKernel} · {readiness.ActiveModel}";
            (ServiceStatusGlyph, ServiceStatusText) = readiness.Level switch
            {
                AiReadinessLevel.Ready => ("\uE73E", "AiHub_ServiceStatus_ReadyFormat".FormatLocalized("Ready · {0}", kernelModel)),
                AiReadinessLevel.Degraded => ("\uE7BA", "AiHub_ServiceStatus_DegradedFormat".FormatLocalized("Main route unavailable, using fallback · {0}", readiness.Detail)),
                AiReadinessLevel.Unverified => ("\uE9CE", "AiHub_ServiceStatus_UnverifiedFormat".FormatLocalized("Configured, not verified yet · {0}", kernelModel)),
                AiReadinessLevel.NotConfigured => ("\uEA39", "AiHub_ServiceStatus_NotConfiguredFormat".FormatLocalized("Not configured: {0}", readiness.Detail)),
                _ => ("\uE711", "AiHub_ServiceStatus_Off".GetLocalizedOrFallback("Off; plugins use rule-based checks only")),
            };
        }
        catch (Exception ex)
        {
            Logger.LogError("Could not read AI readiness state", ex);
        }
    }

    public ICommand SaveCommand { get; }

    public ICommand ClearFallbackCommand { get; }

    public ICommand SavePolicyCommand { get; }

    public ICommand ResetPolicyCommand { get; }

    public ICommand SaveSecurityAuditPolicyCommand { get; }

    public ICommand ResetSecurityAuditPolicyCommand { get; }

    public ICommand SaveOptimizationPolicyCommand { get; }

    public ICommand ResetOptimizationPolicyCommand { get; }

    public ICommand SaveSelfTestPolicyCommand { get; }

    public ICommand ResetSelfTestPolicyCommand { get; }

    public ICommand SaveActivePolicyCommand { get; }

    public ICommand ResetActivePolicyCommand { get; }

    public ICommand DiscardActivePolicyCommand { get; }

    public ICommand CancelOperationCommand { get; }

    public Task InitializeAsync()
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            if (_dispatcherQueue?.HasThreadAccess == true)
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(_dispatcherQueue));
            }

            return InitializePoliciesAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private async Task InitializePoliciesAsync()
    {
        if (_policiesLoading || PoliciesLoaded)
        {
            return;
        }

        _policiesLoading = true;
        try
        {
            await Initialization;
            if (_disposed || IsBusy)
            {
                return;
            }

            if (_loadFailed)
            {
                ShowStatus(Text("AiHub_LoadFailed"), InfoBarSeverity.Error);
                return;
            }

            await RunOperationAsync(async token =>
            {
                string[] policies =
                [
                    await _securityService.LoadGlobalPolicyAsync(token),
                    await _securityService.LoadTaskAgentsPolicyAsync("aihub", "security-audit", token),
                    await _securityService.LoadTaskAgentsPolicyAsync("aihub", "system-optimization", token),
                    await _securityService.LoadTaskAgentsPolicyAsync("aihub", SelfTestPolicy.TaskId, token),
                ];
                token.ThrowIfCancellationRequested();
                for (int index = 0; index < policies.Length; index++)
                {
                    _policyDrafts[index].Load(policies[index]);
                    NotifyPolicyDraftChanged(index);
                }
            }, policyOperation: true);
        }
        finally
        {
            _policiesLoading = false;
        }
    }

    internal void TestTarget(AiTargetSettings target) => StartOperation(async token =>
    {
        if (!RouteDispatcher.IsTargetCompatible(_config.SelectedKernel, target))
        {
            ShowStatus("AiHub_EndpointInvalid".GetLocalizedOrFallback("Check the endpoint URL, model, native API mode, reasoning effort, and context window."), InfoBarSeverity.Warning);
            return;
        }

        ShowStatus($"Testing connection to {target.Name}...", InfoBarSeverity.Informational);
        var testTarget = target.Clone();
        if (string.IsNullOrEmpty(testTarget.ApiKey))
        {
            try
            {
                var saved = _settingsStore.Load();
                var matching = saved.Targets.FirstOrDefault(t => string.Equals(t.Name, testTarget.Name, StringComparison.OrdinalIgnoreCase));
                if (matching != null && !string.IsNullOrEmpty(matching.ApiKey))
                {
                    testTarget.ApiKey = matching.ApiKey;
                }
            }
            catch
            {
            }
        }

        var result = await _routeDispatcher.TestConnectionAsync(testTarget, _config.SelectedKernel, token);
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
            ShowStatus("AiHub_EndpointInvalid".GetLocalizedOrFallback("Check the endpoint URL, model, native API mode, reasoning effort, and context window."), InfoBarSeverity.Warning);
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
        var result = await Task.Run(() => _kernelManager.DownloadOrUpdateAsync(PendingKernel, progress, token), token);
        RefreshLocalKernelStatus();
        KernelLatestText = $"Latest: {result.LatestVersion}";
        ShowStatus(result.Changed ? $"{AiKernelCatalog.GetDisplayName(PendingKernel)} installed successfully." : $"{AiKernelCatalog.GetDisplayName(PendingKernel)} is already up to date.", InfoBarSeverity.Success);
    }

    private void SaveEndpoints()
    {
        try
        {
            if (!TryPrepareEndpointSettings(_config, out var targets))
            {
                ShowStatus("AiHub_EndpointInvalid".GetLocalizedOrFallback("Check the endpoint URL, model, native API mode, reasoning effort, and context window."), InfoBarSeverity.Warning);
                return;
            }

            _settingsStore.Update(config => config.Targets = targets);
            _config.Targets[1].IsActive = targets[1].IsActive;
            HasUnsavedEndpointChanges = false;
            AiHubEngine.RaiseStateChanged();
            ShowStatus("AiHub_EndpointsSaved".GetLocalizedOrFallback("Endpoint settings saved."), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus("AiHub_SaveEndpointsFailedFormat".FormatLocalized("Failed to save endpoints: {0}", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Creates the exact endpoint snapshot to validate and save. Filling a fallback URL
    /// activates that snapshot before validation without changing an unsaved draft.
    /// </summary>
    internal static bool TryPrepareEndpointSettings(AiHubConfig config, out ObservableCollection<AiTargetSettings> targets)
    {
        targets = new ObservableCollection<AiTargetSettings>(config.Targets.Select(target => target.Clone()));
        if (targets.Count != 2)
        {
            return false;
        }

        targets[1].IsActive = !string.IsNullOrWhiteSpace(targets[1].BaseUrl);
        return RouteDispatcher.IsTargetCompatible(config.SelectedKernel, targets[0])
            && (!targets[1].IsActive || RouteDispatcher.IsTargetCompatible(config.SelectedKernel, targets[1]));
    }

    /// <summary>Restores the endpoint fields from the persisted configuration.</summary>
    private void DiscardEndpointChanges()
    {
        try
        {
            AiHubConfig saved = _settingsStore.Load();
            for (int index = 0; index < _config.Targets.Count && index < saved.Targets.Count; index++)
            {
                AiTargetSettings target = _config.Targets[index];
                AiTargetSettings source = saved.Targets[index];
                target.IsActive = source.IsActive;
                target.BaseUrl = source.BaseUrl;
                target.ApiKey = source.ApiKey;
                target.Mode = source.Mode;
                target.Model = source.Model;
                target.Effort = source.Effort;
                target.ContextWindowTokens = source.ContextWindowTokens;
            }

            foreach (var endpoint in Endpoints)
            {
                endpoint.NotifyUpdated();
            }

            HasUnsavedEndpointChanges = false;
            IsStatusOpen = false;
        }
        catch (Exception ex)
        {
            ShowStatus("AiHub_RestoreEndpointsFailedFormat".FormatLocalized("Could not restore endpoints: {0}", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task SaveAllAsync(CancellationToken token)
    {
        if (!TryPrepareEndpointSettings(_config, out var targets))
        {
            ShowStatus("AiHub_EndpointInvalid".GetLocalizedOrFallback("Check the endpoint URL, model, native API mode, reasoning effort, and context window."), InfoBarSeverity.Warning);
            return;
        }

        _settingsStore.Update(config => config.Targets = targets);
        _config.Targets[1].IsActive = targets[1].IsActive;
        AiHubEngine.RaiseStateChanged();

        for (int index = 0; index < _policyDrafts.Length; index++)
        {
            if (_policyDrafts[index].IsLoaded && _policyDrafts[index].IsDirty && !string.IsNullOrWhiteSpace(_policyDrafts[index].Content))
            {
                await SavePolicyDraftAsync(index, token);
            }
        }

        ShowStatus(Text("AIHub_SettingsSaved"), InfoBarSeverity.Success);
    }

    private void ClearFallback()
    {
        if (_config.GetFallbackTarget() is { } target)
        {
            target.IsActive = false;
            target.BaseUrl = string.Empty;
            target.ApiKey = string.Empty;
            FallbackEndpoint?.NotifyUpdated();
            HasUnsavedEndpointChanges = true;
            ShowStatus("AiHub_FallbackCleared".GetLocalizedOrFallback("Fallback endpoint cleared. Save to apply."), InfoBarSeverity.Informational);
        }
    }

    private bool CanSavePolicy(int index) => CanEditPolicies && _policyDrafts[index].IsDirty && !string.IsNullOrWhiteSpace(_policyDrafts[index].Content);

    private void StartPolicySave(int index)
    {
        if (CanSavePolicy(index))
        {
            _ = RunOperationAsync(async token =>
            {
                await SavePolicyDraftAsync(index, token);
                ShowStatus(Text("AiHub_PolicySaved"), InfoBarSeverity.Success);
            }, policyOperation: true);
        }
    }

    private async Task SavePolicyDraftAsync(int index, CancellationToken token)
    {
        string snapshot = _policyDrafts[index].Content;
        if (index == 0)
        {
            await _securityService.SaveGlobalPolicyAsync(snapshot, token);
        }
        else
        {
            await _securityService.SaveTaskPolicyAsync(GetTaskPolicyId(index), snapshot, token);
        }

        _policyDrafts[index].AcceptSaved(snapshot);
        NotifyPolicyDraftChanged(index);
    }

    private void StageDefaultPolicy(int index)
    {
        if (CanEditPolicies)
        {
            UpdatePolicyDraft(index, index == 0 ? SecurityPolicyService.GetDefaultPolicyContent() : SecurityPolicyService.GetDefaultTaskPolicy(GetTaskPolicyId(index)));
            ShowStatus("AiHub_PolicyDefaultStaged".GetLocalizedOrFallback("Default template loaded into the draft. Save to apply."), InfoBarSeverity.Informational);
        }
    }

    private void DiscardActivePolicy()
    {
        if (CanEditPolicies && IsCurrentPolicyDirty)
        {
            _policyDrafts[ActivePolicyIndex].Discard();
            NotifyPolicyDraftChanged(ActivePolicyIndex);
            ShowStatus("AiHub_PolicyDraftDiscarded".GetLocalizedOrFallback("Draft discarded; saved policy restored."), InfoBarSeverity.Informational);
        }
    }

    private static string GetTaskPolicyId(int index) => index switch
    {
        1 => "security-audit",
        2 => "system-optimization",
        3 => SelfTestPolicy.TaskId,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private void RefreshLocalKernelStatus()
    {
        string kernel = PendingKernel;
        ApplyLocalKernelStatus(kernel, _kernelManager.GetStatus(kernel));
    }

    private void ApplyLocalKernelStatus(string kernel, AiKernelStatus status)
    {
        KernelStatusText = status.Installed
            ? $"{AiKernelCatalog.GetDisplayName(kernel)}: Installed ({(string.IsNullOrEmpty(status.Version) ? "unknown version" : status.Version)})"
            : $"{AiKernelCatalog.GetDisplayName(kernel)}: Not installed";
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

    private Task RunOperationAsync(Func<CancellationToken, Task> operation, bool policyOperation = false)
    {
        // Native UI callbacks can lack a managed synchronization context. Capture the
        // dispatcher for the entire operation, including policy loads and busy cleanup.
        var previousContext = SynchronizationContext.Current;
        try
        {
            if (_dispatcherQueue?.HasThreadAccess == true)
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(_dispatcherQueue));
            }

            return ExecuteOperationAsync(operation, policyOperation);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private async Task ExecuteOperationAsync(Func<CancellationToken, Task> operation, bool policyOperation)
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = cancellation;
        _policyOperation = policyOperation;
        IsBusy = true;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsEnabled)
            {
                ShowStatus(Text("AiHub_Cancelled"), InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Operation failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _operation = null;
            _policyOperation = false;
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
                if (!_policyOperation)
                {
                    _operation?.Cancel();
                }
                IsStatusOpen = false;
            }

            NotifyEnabledChanged();
            RefreshServiceStatus();
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
        OnPropertyChanged(nameof(CanEditPolicies));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        foreach (var command in new[] { ApplyKernelSwitchCommand, CheckKernelUpdatesCommand, DownloadKernelCommand, SaveCommand, SaveEndpointsCommand, DiscardEndpointChangesCommand, ClearFallbackCommand, SavePolicyCommand, ResetPolicyCommand, SaveSecurityAuditPolicyCommand, ResetSecurityAuditPolicyCommand, SaveOptimizationPolicyCommand, ResetOptimizationPolicyCommand, SaveSelfTestPolicyCommand, ResetSelfTestPolicyCommand, SaveActivePolicyCommand, ResetActivePolicyCommand, DiscardActivePolicyCommand, CancelOperationCommand })
        {
            ((RelayCommand)command).OnCanExecuteChanged();
        }

        foreach (var endpoint in Endpoints)
        {
            ((RelayCommand)endpoint.TestCommand).OnCanExecuteChanged();
        }
    }

    private static string Text(string key) => ResourceLoaderInstance.ResourceLoader.GetString(key);

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
        DisposeDiagnostics();
        if (_engine is not null)
        {
            _engine.StateChanged -= OnHubStateChanged;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
