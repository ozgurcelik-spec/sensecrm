using Sense.Crm.Shared.Contracts.Events;

namespace Sense.Crm.Modules.Service.Contracts;

/// <summary>
/// Talep çözüldüğünde (<c>→ resolved</c> veya çözülmeden <c>→ closed</c>; <c>resolvedAt</c> yazılan her geçiş) komutla aynı
/// transaction'da outbox'a yazılan integration event (K10). Yeniden açılıp tekrar çözülen talep yeni bir olay üretir.
/// <see cref="ResolutionMinutes"/> oluşturmadan çözüme tam dakika (aşağı yuvarlanır); <see cref="SlaBreached"/> çözüm anındaki SLA değerlendirmesidir.
/// Bugün tüketicisi yoktur (bildirim/workflow için hazır).
/// </summary>
public sealed record CaseResolved(
    Guid TenantId,
    Guid CaseId,
    string CaseNumber,
    Guid? AccountId,
    Guid? ContactId,
    string Priority,
    Guid? AssignedUserId,
    DateTime ResolvedAt,
    int ResolutionMinutes,
    bool SlaBreached,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);
