// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library.Helpers;

namespace Kit.Settings.UI.ViewModels;

public sealed class OptimizationCategoryViewModel : Observable
{
    private int _processed;
    private bool _isEnabled;
    private bool _isExpanded;

    public OptimizationCategoryViewModel(IReadOnlyList<TempFileInfo> items, int approvedCount, Action execute)
    {
        Items = items;
        ApprovedCount = approvedCount;
        Name = items[0].Category;
        Path = string.IsNullOrEmpty(items[0].CacheRoot) ? System.IO.Path.GetDirectoryName(items[0].FilePath) : items[0].CacheRoot;
        Size = new TempFileInfo { SizeInBytes = items.Sum(item => item.SizeInBytes) }.SizeFormatted;
        ActionLabel = AIHubPageViewModel.IsChinese ? (items[0].Action == "move" ? "整理" : "清理") : (items[0].Action == "move" ? "Organize" : "Clean");
        ExecuteCommand = new RelayCommand(execute);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value && !HasBeenExpanded)
            {
                HasBeenExpanded = true;
                OnPropertyChanged(nameof(HasBeenExpanded));
            }
        }
    }

    // Keep lazily loaded details alive so collapsing does not tear down the file list.
    public bool HasBeenExpanded { get; private set; }
    public IReadOnlyList<TempFileInfo> Items { get; }
    public string Family => Name.StartsWith("VS Code", StringComparison.Ordinal) || Name == "Visual Studio indexes" ? "editors"
        : !string.IsNullOrEmpty(Items[0].DevelopmentRepository) ? "development"
        : Name.StartsWith(".NET workload", StringComparison.Ordinal) || Name.StartsWith("uv cache", StringComparison.Ordinal) || Name.StartsWith("NuGet", StringComparison.Ordinal) || Name.StartsWith("pip", StringComparison.Ordinal) || Name.StartsWith("npm", StringComparison.Ordinal) || Name.StartsWith("pnpm", StringComparison.Ordinal) ? "packages"
        : Name.StartsWith("Edge", StringComparison.Ordinal) || Name.StartsWith("Chrome", StringComparison.Ordinal) ? "browsers" : "system";
    public int ApprovedCount { get; }
    public string Destination => Items[0].Action == "move" ? $"{Path} → {Items[0].Category}" : Path;
    public string LocationLabel => string.IsNullOrEmpty(Items[0].DevelopmentRepository) ? Destination
        : System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(Items[0].DevelopmentRepository), Path);
    public string ReviewSummary => AIHubPageViewModel.IsChinese
        ? $"{Items.Count:N0} 个文件 · {ApprovedCount:N0} 个可执行"
        : $"{Items.Count:N0} files · {ApprovedCount:N0} actionable";
    public string ReviewStatus => Items.Any(item => !string.IsNullOrEmpty(item.ExecutionError))
        ? (AIHubPageViewModel.IsChinese ? "操作失败，可重试" : "Action failed; retry available")
        : ApprovedCount > 0
        ? (AIHubPageViewModel.IsChinese ? "AI 已通过" : "AI approved")
        : Items.Any(item => item.ReviewState == "failed")
        ? (AIHubPageViewModel.IsChinese ? "AI 审核未完成" : "AI review incomplete")
        : Items.All(item => item.ReviewState == "skipped")
        ? (AIHubPageViewModel.IsChinese ? "AI 已跳过" : "AI skipped")
        : (AIHubPageViewModel.IsChinese ? "等待 AI 审核" : "Awaiting AI review");
    public bool HasProgress => Processed > 0;
    public string Name { get; }
    public string Path { get; }
    public string Size { get; }
    public string ActionLabel { get; }
    public ICommand ExecuteCommand { get; }
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
    public int Processed
    {
        get => _processed;
        set
        {
            if (Set(ref _processed, value))
            {
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(HasProgress));
            }
        }
    }
    public double Progress => ApprovedCount == 0 ? 0 : 100.0 * Processed / ApprovedCount;
    public string Summary => Processed == 0 ? Size : $"{Processed:N0}/{ApprovedCount:N0} · {Size}";
}
