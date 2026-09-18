using System;

namespace Kit.AIHubLib.Models;

public sealed record SecurityEvent(
    int EventId,
    string LogName,
    string ProviderName,
    int? Level,
    DateTime? TimeCreated,
    string? Message,
    string? RawXml = null);
