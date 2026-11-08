// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library.Helpers;
using ManagedCommon;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    private readonly OptimizationInventoryService _inventoryService = new();
    private RelayCommand _analyzeStorageCommand;
    private bool _isInventoryScanning;
    private string _inventoryStatusText = "AIHub_Inventory_StatusReady".GetLocalizedOrFallback("Analyze common caches and managed storage without changing files.");

    public OptimizationInventoryViewModel Inventory { get; } = new();

    public bool HasInventory => Inventory.HasRows;

    public bool CanAnalyzeStorage => !_disposed && CanSelectOptimizationItems;

    public bool CanCancelInventory => IsInventoryScanning;

    public ICommand AnalyzeStorageCommand => _analyzeStorageCommand ??= new RelayCommand(RunStorageInventory, () => CanAnalyzeStorage);

    public ICommand CancelInventoryCommand => CancelOptimizationCommand;

    public string InventoryStatusText
    {
        get => _inventoryStatusText;
        private set => Set(ref _inventoryStatusText, value);
    }

    public bool IsInventoryScanning
    {
        get => _isInventoryScanning;
        private set
        {
            if (Set(ref _isInventoryScanning, value))
            {
                OnPropertyChanged(nameof(CanCancelInventory));
            }
        }
    }

    private void InitializeInventory() => Inventory.PropertyChanged += OnInventoryChanged;

    private void OnInventoryChanged(object sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(OptimizationInventoryViewModel.HasRows))
        {
            OnPropertyChanged(nameof(HasInventory));
        }
    }

    private void RefreshInventoryCommands()
    {
        _analyzeStorageCommand?.OnCanExecuteChanged();
        OnPropertyChanged(nameof(CanAnalyzeStorage));
    }

    private void BeginInventoryScan()
    {
        IsInventoryScanning = true;
        InventoryStatusText = "AIHub_Inventory_StatusScanning".GetLocalizedOrFallback("Reading file sizes and timestamps. No file contents are read or changed.");
    }

    private void ApplyInventoryReport(OptimizationInventoryReport report)
    {
        if (report is null || report.CompletedUtc == default)
        {
            return;
        }

        Inventory.Apply(report);
        IsInventoryScanning = false;
        InventoryStatusText = "AIHub_Inventory_StatusComplete".GetLocalizedOrFallback("Storage analysis complete.");
        OnPropertyChanged(nameof(HasInventory));
    }

    private void RunStorageInventory() => _ = RunStorageInventoryAsync(_inventoryService.ScanAsync);

    private Task RunStorageInventoryAsync(Func<CancellationToken, Task<OptimizationInventoryReport>> scan)
    {
        if (!CanAnalyzeStorage)
        {
            return Task.CompletedTask;
        }

        _optimizationCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _optimizationCts = cancellation;
        CancellationToken token = cancellation.Token;
        IsOptimizing = true;
        BeginInventoryScan();
        ResetScanProgress();
        ScanPhase = ScanPhase.Scanning;
        OptimizationWorkflowStatus = InventoryStatusText;
        BeginStage("Scan cache locations");

        return Task.Run(async () =>
        {
            try
            {
                OptimizationInventoryReport report = await scan(token).ConfigureAwait(false);
                EnqueueOnUI(() =>
                {
                    if (_optimizationCts != cancellation)
                    {
                        return;
                    }

                    ApplyInventoryReport(report);
                    EndStage("Finish");
                    ScanPhase = ScanPhase.SelectingTargets;
                    OptimizationWorkflowStatus = InventoryStatusText;
                }, token);
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() => FinishInventoryInterruption(cancellation, cancelled: true));
            }
            catch (Exception exception)
            {
                Logger.LogWarning($"Storage inventory failed: type={exception.GetType().Name}");
                EnqueueOnUI(() => FinishInventoryInterruption(cancellation, cancelled: false));
            }
            finally
            {
                EnqueueOnUI(() =>
                {
                    if (_optimizationCts == cancellation)
                    {
                        if (token.IsCancellationRequested)
                        {
                            FinishInventoryInterruption(cancellation, cancelled: true);
                        }

                        IsInventoryScanning = false;
                        IsOptimizing = false;
                    }
                });
            }
        });
    }

    private void FinishInventoryInterruption(CancellationTokenSource operation, bool cancelled)
    {
        if (_optimizationCts != operation)
        {
            return;
        }

        InventoryStatusText = cancelled
            ? "AIHub_Inventory_StatusCancelled".GetLocalizedOrFallback("Analysis cancelled. The previous inventory is retained.")
            : "AIHub_Inventory_StatusFailed".GetLocalizedOrFallback("Analysis could not finish. The previous inventory is retained.");
        OptimizationWorkflowStatus = InventoryStatusText;
        _scanProgress.Reset();
        ScanPhase = cancelled ? ScanPhase.Idle : ScanPhase.Failed;
        PublishScanProgress();
    }
}
