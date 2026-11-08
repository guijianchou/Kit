namespace Kit.AiHub.Models;

using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Configuration for an AI endpoint target (Main or Fallback).
/// </summary>
public sealed partial class AiTargetSettings : ObservableObject
{
    public const string DefaultLunaModel = "gpt-6-luna";
    public const string DefaultTerraModel = "gpt-5.6-terra";
    public const string DefaultSolModel = "gpt-5.6-sol";
    public const string DefaultAstraModel = "gpt-6-astra";
    public const int DefaultContextWindowTokens = 256000;
    public const int MinimumContextWindowTokens = 1024;

    private string _name = string.Empty;
    private bool _isActive;
    private string _baseUrl = string.Empty;
    private string _apiKey = string.Empty;
    private string _mode = "responses";
    private string _model = DefaultLunaModel;
    private string _effort = "high";
    private int _contextWindowTokens = DefaultContextWindowTokens;
    private bool _isExpanded;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    [JsonIgnore]
    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public string Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    public string Effort
    {
        get => _effort;
        set => SetProperty(ref _effort, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Context-window size passed to the native kernel for this endpoint.
    /// </summary>
    public int ContextWindowTokens
    {
        get => _contextWindowTokens;
        set => SetProperty(ref _contextWindowTokens, value);
    }

    /// <summary>
    /// Checks configuration syntax only; the native kernel validates supported values.
    /// </summary>
    public static bool IsValidNativeIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates a deep copy of the settings.
    /// </summary>
    public AiTargetSettings Clone() => new()
    {
        Name = Name,
        IsActive = IsActive,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Mode = Mode,
        Model = Model,
        Effort = Effort,
        ContextWindowTokens = ContextWindowTokens,
        IsExpanded = IsExpanded
    };
}
