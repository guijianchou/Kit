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
        CancelOperationCommand = new RelayCommand(() => _operation?.Cancel(), () => IsBusy);
        BuildEndpoints();
        if (_engine is not null)
        {
            _engine.StateChanged += OnHubStateChanged;
        }

        RefreshLocalKernelStatus();
        if (IsEnabled)
        {
            _ = InitializeAsync();
        }
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

    public string SecurityPolicyStatusText { get => _securityPolicyStatusText; private set => Set(ref _securityPolicyStatusText, value); }

    public string SecurityPolicyContent
    {
        get => _securityPolicyContent;
        set
        {
            if (Set(ref _securityPolicyContent, value))
            {
                SecurityPolicyStatusText = "Draft";
                ((RelayCommand)SavePolicyCommand).OnCanExecuteChanged();
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

        if (!IsEnabled)
        {
            return;
        }

        await RunOperationAsync(async token =>
        {
            RefreshLocalKernelStatus();
            SecurityPolicyContent = await _securityService.LoadGlobalPolicyAsync(token);
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
        foreach (var command in new[] { ApplyKernelSwitchCommand, CheckKernelUpdatesCommand, DownloadKernelCommand, SaveCommand, SaveEndpointsCommand, ClearFallbackCommand, SavePolicyCommand, ResetPolicyCommand, CancelOperationCommand })
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
