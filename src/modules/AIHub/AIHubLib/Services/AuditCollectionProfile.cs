using System;

namespace Kit.AIHubLib.Services;

/// <summary>Versioned collection and scoring identity for comparable audit summaries.</summary>
public static class AuditCollectionProfile
{
    // Increment when channel queries, event allowlists, rule classifications or score weights change.
    public static string ForMode(EventLogService.AuditMode mode) => mode switch
    {
        EventLogService.AuditMode.Extended => "rules-v1|extended|System,Application,Setup,ForwardedEvents",
        EventLogService.AuditMode.Full => "rules-v1|full|System,Application,Setup,ForwardedEvents,Security,Firewall",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
