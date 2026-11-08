// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Controls;

/// <summary>
/// Keeps an editable value visible when the native ComboBox creates its template
/// after the value was loaded, including values outside its suggestion list.
/// </summary>
public sealed partial class EditableValueComboBox : ComboBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(EditableValueComboBox), new PropertyMetadata(string.Empty, OnValueChanged));

    private bool _synchronizing;

    public EditableValueComboBox()
    {
        IsEditable = true;
        RegisterPropertyChangedCallback(TextProperty, OnTextChanged);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty) ?? string.Empty;
        set => SetValue(ValueProperty, value ?? string.Empty);
    }

    protected override void OnApplyTemplate()
    {
        // Template setup may reset native selection/text. It must not overwrite
        // the saved value or turn initialization into an unsaved endpoint edit.
        bool wasSynchronizing = _synchronizing;
        _synchronizing = true;
        try
        {
            base.OnApplyTemplate();
        }
        finally
        {
            _synchronizing = wasSynchronizing;
        }

        SynchronizeText();
    }

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((EditableValueComboBox)sender).SynchronizeText();

    private void OnTextChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_synchronizing)
        {
            return;
        }

        _synchronizing = true;
        try
        {
            Value = Text;
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void SynchronizeText()
    {
        if (_synchronizing)
        {
            return;
        }

        _synchronizing = true;
        try
        {
            string value = Value;
            SelectedItem = string.IsNullOrEmpty(value) ? null : value;

            // WinUI's unfocused presenter is separate from EditableText. A Text
            // change after template creation refreshes it even for custom values.
            // Suppress the intermediate empty text from reaching the bound draft.
            Text = string.Empty;
            Text = value;
        }
        finally
        {
            _synchronizing = false;
        }
    }
}
