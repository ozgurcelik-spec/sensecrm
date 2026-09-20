using Sense.Crm.Shared.Contracts.Events;

namespace Sense.Crm.Modules.Platform.Contracts;

// Platform integration event'leri (docs/plan/m7-saas-hazirlik.md): IIntegrationEventOutbox.Enqueue ile işlemle aynı SaveChanges'te Platform
// outbox'ına yazılır. M7'de tüketici yoktur (testler outbox satırını doğrular); gelecekteki faturalama/bildirim modülü bunları dinler.
// İmha sonrası TenantErased dışındaki eski olay satırları kiracıyla birlikte silinir.

/// <summary>Kiracı askıya alındı (<paramref name="Mode"/>: <c>readOnly</c> | <c>blocked</c>).</summary>
public sealed record TenantSuspended(Guid TenantId, string Reason, string Mode, Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Askı kaldırıldı.</summary>
public sealed record TenantReactivated(Guid TenantId, Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Plan, istisna <b>veya</b> deneme tarihi değişti (<paramref name="OverridesChanged"/> istisna değişimini ayırt eder).</summary>
public sealed record PlanChanged(Guid TenantId, string? OldPlanCode, string NewPlanCode, DateOnly? TrialEndsOn, bool OverridesChanged, Guid? ActorUserId = null)
    : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Silme talebi açıldı (kiracı anında <c>pending_deletion</c>).</summary>
public sealed record TenantDeletionRequested(Guid TenantId, DateTimeOffset ScheduledFor, Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Silme talebi bekleme süresinde iptal edildi.</summary>
public sealed record TenantDeletionCancelled(Guid TenantId, Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Kiracı verisi kalıcı olarak imha edildi.</summary>
public sealed record TenantErased(Guid TenantId, DateTimeOffset ErasedAt) : IntegrationEvent(TenantId);
