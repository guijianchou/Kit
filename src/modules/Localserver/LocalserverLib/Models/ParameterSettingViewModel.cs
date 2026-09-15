// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace LocalServerHub.Core.Models
{
    /// <summary>
    /// One editable row of a line's flag knowledge base.
    /// </summary>
    public sealed class ParameterSettingViewModel : INotifyPropertyChanged
    {
        private static readonly string[] TypeNames =
            [.. Enum.GetNames<ParameterType>()];

        private readonly ParameterDefinition _original;

        private string _flag;
        private string _name;
        private string _typeName;
        private string _defaultText;
        private bool _usesEquals;
        private bool _bindsToPort;

        public ParameterSettingViewModel()
            : this(new ParameterDefinition { Flag = string.Empty, Name = string.Empty })
        {
        }

        public ParameterSettingViewModel(ParameterDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);

            _original = definition;
            _flag = definition.Flag;
            _name = definition.Name;
            _typeName = definition.Type.ToString();
            _defaultText = FormatDefault(definition.Default);
            _usesEquals = definition.Style == ParameterValueStyle.Equals;
            _bindsToPort = string.Equals(definition.BindsTo, "port", StringComparison.OrdinalIgnoreCase);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public IReadOnlyList<string> TypeOptions => TypeNames;

        private bool _isLocked;

        /// <summary>
        /// Set by the owning line while its service is running.
        /// </summary>
        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                if (Set(ref _isLocked, value))
                {
                    Raise(nameof(IsEditable));
                }
            }
        }

        /// <summary>Disabled rather than hidden, so the flag stays readable while running.</summary>
        public bool IsEditable => !_isLocked;

        public string Flag
        {
            get => _flag;
            set => Set(ref _flag, value ?? string.Empty, alsoPreview: true);
        }

        public string Name
        {
            get => _name;
            set => Set(ref _name, value ?? string.Empty);
        }

        public string TypeName
        {
            get => _typeName;
            set
            {
                if (!Set(ref _typeName, value ?? nameof(ParameterType.Boolean), alsoPreview: true))
                {
                    return;
                }

                Raise(nameof(DefaultPlaceholder));
            }
        }

        public string DefaultText
        {
            get => _defaultText;
            set => Set(ref _defaultText, value ?? string.Empty, alsoPreview: true);
        }

        public bool UsesEquals
        {
            get => _usesEquals;
            set => Set(ref _usesEquals, value, alsoPreview: true);
        }

        public bool BindsToPort
        {
            get => _bindsToPort;
            set => Set(ref _bindsToPort, value, alsoPreview: true);
        }

        public ParameterType Type =>
            Enum.TryParse(_typeName, out ParameterType parsed) ? parsed : ParameterType.Boolean;

        public string DefaultPlaceholder => Type switch
        {
            ParameterType.Boolean => "false",
            ParameterType.Number => "8080",
            ParameterType.Path => @"C:\path\to\file",
            ParameterType.Secret => "(stored separately)",
            ParameterType.Enum => _original.Options.Count > 0 ? _original.Options[0] : "value",
            _ => "value",
        };

        public string PreviewText
        {
            get
            {
                string flag = _flag.Trim();
                if (flag.Length == 0)
                {
                    return "Enter a flag to see how it is emitted.";
                }

                if (Type == ParameterType.Boolean)
                {
                    bool on = bool.TryParse(_defaultText.Trim(), out bool parsed) && parsed;
                    return on ? $"Emits {flag}" : $"Emits {flag} only when enabled";
                }

                string value = _defaultText.Trim();
                if (value.Length == 0)
                {
                    value = DefaultPlaceholder;
                }

                if (Type == ParameterType.Secret)
                {
                    value = "***";
                }

                return _usesEquals ? $"Emits {flag}={value}" : $"Emits {flag} {value}";
            }
        }

        public ParameterDefinition ToDefinition()
        {
            string flag = _flag.Trim();
            string name = _name.Trim();
            ParameterType type = Type;

            return _original with
            {
                Flag = flag,
                Name = name.Length > 0 ? name : flag,
                Type = type,
                Default = ParseDefault(type, _defaultText),
                Style = _usesEquals ? ParameterValueStyle.Equals : ParameterValueStyle.Space,
                BindsTo = _bindsToPort
                    ? "port"
                    : string.Equals(_original.BindsTo, "port", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : _original.BindsTo,
            };
        }

        public bool IsEmpty => _flag.Trim().Length == 0;

        private static string FormatDefault(object? value) => value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            string text => text,
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        private static object? ParseDefault(ParameterType type, string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return type == ParameterType.Boolean ? false : null;
            }

            return type switch
            {
                ParameterType.Boolean => bool.TryParse(trimmed, out bool flag) && flag,
                ParameterType.Number =>
                    double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                        ? number
                        : trimmed,
                _ => trimmed,
            };
        }

        private bool Set<T>(
            ref T field,
            T value,
            bool alsoPreview = false,
            [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            Raise(propertyName);
            if (alsoPreview)
            {
                Raise(nameof(PreviewText));
            }

            return true;
        }

        private void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
