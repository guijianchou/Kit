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

public sealed class OptimizationGroupViewModel : Observable
{
    private bool _isEnabled;
    private bool _isExpanded;

    public OptimizationGroupViewModel(string family, IReadOnlyList<OptimizationCategoryViewModel> categories, Action execute)
    {
        Categories = categories;
        Family = family;
        Name = AIHubPageViewModel.IsChinese ? family switch
        {
            "development" => "开发项目",
            "packages" => "包管理器",
            "editors" => "编辑器与 IDE",
            "browsers" => "浏览器",
            _ => "系统缓存",
        }
        : family switch
        {
            "development" => "Development projects",
            "packages" => "Package managers",
            "editors" => "Editors & IDEs",
            "browsers" => "Browsers",
            _ => "System caches",
        };
        ApprovedCount = categories.Sum(category => category.ApprovedCount);
        Size = new TempFileInfo { SizeInBytes = categories.Sum(category => category.Items.Sum(item => item.SizeInBytes)) }.SizeFormatted;
        int files = categories.Sum(category => category.Items.Count);
        Summary = AIHubPageViewModel.IsChinese
            ? $"{categories.Count:N0} 个位置/类型 · {files:N0} 个文件 · {ApprovedCount:N0} 个可执行"
            : $"{categories.Count:N0} locations/types · {files:N0} files · {ApprovedCount:N0} actionable";
        CleanLabel = AIHubPageViewModel.IsChinese ? (family == "project" ? "清理项目" : "清理本类") : (family == "project" ? "Clean project" : "Clean group");
        ExecuteCommand = new RelayCommand(execute);
    }

    public IReadOnlyList<OptimizationCategoryViewModel> Categories { get; }
    public IReadOnlyList<OptimizationGroupViewModel> Projects { get; init; } = Array.Empty<OptimizationGroupViewModel>();
    public bool HasProjects => Projects.Count > 0;
    public bool HasDirectCategories => !HasProjects;
    public string Location { get; init; } = string.Empty;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public string Glyph => Family switch
    {
        "development" => "\uE943",
        "packages" => "\uE7B8",
        "editors" => "\uE70F",
        "browsers" => "\uE774",
        _ => "\uE713",
    };
    public string Family { get; }
    public string Name { get; init; }
    public string Size { get; }
    public string Summary { get; }
    public int ApprovedCount { get; }
    public string CleanLabel { get; }
    public ICommand ExecuteCommand { get; }
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
}
