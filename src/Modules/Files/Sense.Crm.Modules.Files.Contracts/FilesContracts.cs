using Sense.Crm.Shared.Contracts.Events;

namespace Sense.Crm.Modules.Files.Contracts;

/// <summary>
/// Dosya eklendi (M8C; <c>IIntegrationEventOutbox.Enqueue</c> ile işlemle aynı <c>SaveChanges</c>'te). Tüketici yoktur; testler outbox satırını doğrular.
/// <b>Dosya adı taşımaz</b> (kişisel veri olabilir).
/// </summary>
public sealed record FileAttached(Guid TenantId, Guid FileId, string RecordType, Guid RecordId, long SizeBytes, string ContentType, Guid? ActorUserId = null)
    : IntegrationEvent(TenantId, ActorUserId);

/// <summary>
/// Dosya silindi (kullanıcı yumuşak silmesi ve kayıt-yok süpürmesi; süpürme <c>ActorUserId = null</c> ile yayınlar). Fiziksel temizlik yayınlamaz.
/// </summary>
public sealed record FileDeleted(Guid TenantId, Guid FileId, string RecordType, Guid RecordId, Guid? ActorUserId = null)
    : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Files varlık türlerinin denetim kaydındaki adları (K14: CLR tip adı).</summary>
public static class FilesAuditEntities
{
    /// <summary>Plan "file" der; denetim kaydı <c>EntityType</c>'ı CLR tip adıdır (K14) → <c>FileAttachment</c>.</summary>
    public const string File = "FileAttachment";
}
