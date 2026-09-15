namespace Kit.AiHub.Models;

using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Configuration for an AI endpoint target (Main or Fallback).
/// </summary>
public sealed partial class AiTargetSettings : ObservableObject
{
    public const string DefaultLunaModel = "gpt-5.6-luna";
    public const string DefaultTerraModel = "gpt-5.6-terra";
    public const string DefaultSolModel = "gpt-5.6-sol";
    public const string DefaultAstraModel = "gpt-6-astra";

    private string _name = string.Empty;
    private bool _isActive;
    private string _baseUrl = string.Empty;
    private string _apiKey = string.Empty;
    private string _mode = "responses"; // "chat" or "responses"
    private string _model = DefaultLunaModel;
    private string _effort = "medium"; // low, medium, high, xhigh, max
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
        IsExpanded = IsExpanded
    };
}
