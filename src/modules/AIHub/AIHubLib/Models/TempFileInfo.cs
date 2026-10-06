using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Kit.AIHubLib.Models;

public enum RiskLevel
{
    Low,
    Medium,
    High
}

public sealed partial class TempFileInfo : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string ItemId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeInBytes { get; set; }
    public DateTime LastModified { get; set; }
    public string Category { get; set; } = string.Empty;
    public string CacheRoot { get; set; } = string.Empty;
    public string DevelopmentRepository { get; set; } = string.Empty;
    public string BuildManifestPath { get; set; } = string.Empty;
    public string ReviewState { get; set; } = "pending";
    public string ExecutionError { get; set; } = string.Empty;
    public string ReviewLabel => System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? ReviewState switch { "approved" => "AI 已通过", "skipped" => "AI 已跳过", "failed" => "AI 未完成", _ => "等待 AI 审核" }
        : ReviewState switch { "approved" => "AI approved", "skipped" => "AI skipped", "failed" => "AI incomplete", _ => "Awaiting AI review" };
    public string DisplayReason => !string.IsNullOrEmpty(ExecutionError) ? ExecutionError : System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? ReasonZh : ReasonEn;
    public RiskLevel Risk { get; set; } = RiskLevel.Low;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public string? SkipReason { get; set; }

    public string Action { get; set; } = "delete"; // "move" or "delete" or "skip"
    public string? TargetRelativePath { get; set; }
    public string ReasonEn { get; set; } = string.Empty;
    public string ReasonZh { get; set; } = string.Empty;

    public string SizeFormatted
    {
        get
        {
            if (SizeInBytes >= 1024 * 1024 * 1024)
            {
                return $"{SizeInBytes / (1024.0 * 1024 * 1024):F1} GB";
            }
            if (SizeInBytes >= 1024 * 1024)
            {
                return $"{SizeInBytes / (1024.0 * 1024):F1} MB";
            }
            if (SizeInBytes >= 1024)
            {
                return $"{SizeInBytes / 1024.0:F1} KB";
            }
            return $"{SizeInBytes} B";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
