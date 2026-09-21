// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.Settings.UI.Helpers;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>
/// Shared audit progress surface.
/// </summary>
/// <remarks>
/// The audit runs on the AI Hub page, but the progress belongs in the navigation pane,
/// which outlives any single page. Both sides talk to this small observable holder
/// instead of one reaching into the other. Mirrors the original app, whose sidebar hosts
/// the workflow progress next to the navigation items.
/// </remarks>
public sealed class AuditProgressState : INotifyPropertyChanged
{
    /// <summary>Process-wide instance shared by the page and the shell.</summary>
    public static AuditProgressState Current { get; } = new();

    private bool _isVisible;
    private double _percent;
    private string _title = string.Empty;
    private string _glyph = "\uEA3A";
    private string _percentText = "0%";
    private string _countText = string.Empty;
    private string _tooltip = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the pane should show the workflow block.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    public double Percent
    {
        get => _percent;
        set => Set(ref _percent, value);
    }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Glyph
    {
        get => _glyph;
        set => Set(ref _glyph, value);
    }

    public string PercentText
    {
        get => _percentText;
        set => Set(ref _percentText, value);
    }

    public string CountText
    {
        get => _countText;
        set => Set(ref _countText, value);
    }

    public string Tooltip
    {
        get => _tooltip;
        set => Set(ref _tooltip, value);
    }

    /// <summary>Replaces every field in one pass so the pane never sees a half state.</summary>
    public void Update(bool visible, double percent, string title, string glyph, string percentText, string countText, string tooltip)
    {
        IsVisible = visible;
        Percent = percent;
        Title = title;
        Glyph = glyph;
        PercentText = percentText;
        CountText = countText;
        Tooltip = tooltip;
    }

    private void Set<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value))
        {
            return;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
