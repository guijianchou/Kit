// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Helpers;

namespace Kit.Settings.UI.Library;

public sealed class AdvancedPasteCustomAction : Observable, IAdvancedPasteAction, ICloneable
{
    private int _id;
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _prompt = string.Empty;
    private bool _isShown;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private bool _isValid;

    [JsonPropertyName("id")]
    public int Id
    {
        get => _id;
        set => Set(ref _id, value);
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                UpdateIsValid();
            }
        }
    }

    [JsonPropertyName("description")]
    public string Description
    {
        get => _description;
        set => Set(ref _description, value ?? string.Empty);
    }

    [JsonPropertyName("prompt")]
    public string Prompt
    {
        get => _prompt;
        set
        {
            if (Set(ref _prompt, value))
            {
                UpdateIsValid();
            }
        }
    }

    [JsonPropertyName("isShown")]
    public bool IsShown
    {
        get => _isShown;
        set => Set(ref _isShown, value);
    }

    [JsonIgnore]
    public bool CanMoveUp
    {
        get => _canMoveUp;
        set => Set(ref _canMoveUp, value);
    }

    [JsonIgnore]
    public bool CanMoveDown
    {
        get => _canMoveDown;
        set => Set(ref _canMoveDown, value);
    }

    [JsonIgnore]
    public bool IsValid
    {
        get => _isValid;
        private set => Set(ref _isValid, value);
    }

    [JsonIgnore]
    public IEnumerable<IAdvancedPasteAction> SubActions => [];

    public object Clone()
    {
        AdvancedPasteCustomAction clone = new();
        clone.Update(this);
        return clone;
    }

    public void Update(AdvancedPasteCustomAction other)
    {
        Id = other.Id;
        Name = other.Name;
        Description = other.Description;
        Prompt = other.Prompt;
        IsShown = other.IsShown;
        CanMoveUp = other.CanMoveUp;
        CanMoveDown = other.CanMoveDown;
    }

    private void UpdateIsValid()
    {
        IsValid = !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Prompt);
    }
}
