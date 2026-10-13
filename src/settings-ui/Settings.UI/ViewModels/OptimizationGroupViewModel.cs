// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
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
        Name = family switch
        {
            "development" => Text("AIHub_Group_Development"),
            "packages" => Text("AIHub_Group_Packages"),
            "editors" => Text("AIHub_Group_Editors"),
            "browsers" => Text("AIHub_Group_Browsers"),
            _ => Text("AIHub_Group_System"),
        };
        ApprovedCount = categories.Sum(category => category.ApprovedCount);
        Size = new TempFileInfo { SizeInBytes = categories.Sum(category => category.Items.Sum(item => item.SizeInBytes)) }.SizeFormatted;
        int files = categories.Sum(category => category.Items.Count);
        Summary = string.Format(CultureInfo.CurrentCulture, Text("AIHub_Group_Summary"), categories.Count, files, ApprovedCount);
        CleanLabel = family == "project" ? Text("AIHub_Clean_Project") : Text("AIHub_Clean_Group");
        ExecuteCommand = new RelayCommand(execute);
    }

    private static string Text(string key) => ResourceLoaderInstance.ResourceLoader.GetString(key);

    public IReadOnlyList<OptimizationCategoryViewModel> Categories { get; }
    public IReadOnlyList<OptimizationGroupViewModel> Projects { get; init; } = Array.Empty<OptimizationGroupViewModel>();
    public bool HasProjects => Projects.Count > 0;
    public bool HasDirectCategories => !HasProjects;
    public string Location { get; init; } = string.Empty;
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

    // Keep lazily loaded details alive so collapsing does not tear down nested lists.
    public bool HasBeenExpanded { get; private set; }
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
