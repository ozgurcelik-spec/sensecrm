namespace Sense.Crm.Modules.Platform.Domain.Audit;

/// <summary>
/// Platform denetim satırı (append-only, küresel): kim (aktör e-postası anlık görüntü), hangi kiracıda (ad anlık görüntü), ne yaptı;
/// <c>details</c> = <c>{ alan: { old, new } }</c> + <c>reason</c> — <b>kiracı iş verisi/kişisel veri yok</b>. Kiracı denetim tablosuna
/// (<c>audit.audit_log_entries</c>) yazılmaz. İmhadan sonra satır kalır, hedef kiracı adı redakte edilir.
/// </summary>
public sealed class PlatformAuditEntry
{
    public Guid Id { get; set; }

    public DateTime OccurredAt { get; set; }

    public Guid? ActorUserId { get; set; }

    public string? ActorEmail { get; set; }

    public string Action { get; set; } = string.Empty;

    public Guid? TargetTenantId { get; set; }

    public string? TargetTenantName { get; set; }

    /// <summary>jsonb.</summary>
    public string Details { get; set; } = "{}";

    public string? Ip { get; set; }

    public string? CorrelationId { get; set; }
}
