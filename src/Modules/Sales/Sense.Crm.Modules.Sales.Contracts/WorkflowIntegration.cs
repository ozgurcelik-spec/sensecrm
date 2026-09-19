using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Sales.Contracts;

/// <summary>
/// Yeni potansiyel müşteri (lead) oluşturulduğunda yayınlanan integration event (M4 workflow tetikleyicisi).
/// Oluşturma transaction'ıyla aynı anda outbox'a yazılır. <see cref="Source"/> camelCase kaynak adıdır
/// (<c>web|referral|campaign|coldCall|other</c>).
/// </summary>
public sealed record LeadCreated(
    Guid TenantId,
    Guid LeadId,
    string LeadName,
    string Company,
    string Source,
    Guid OwnerUserId,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Aşama türleri (tel biçimi camelCase string): <c>open|won|lost</c>.</summary>
public static class DealStageKinds
{
    public const string Open = "open";
    public const string Won = "won";
    public const string Lost = "lost";
}

/// <summary>
/// Fırsatın aşaması değiştiğinde yayınlanan integration event (Sales'in <c>DealStageChanged</c> domain event'inden türetilir;
/// M4 workflow tetikleyicisi). <see cref="EventId"/> domain event kimliğinden deterministik türetilir: aynı domain event tekrar
/// işlense de aynı kimlik doğar (tüketiciler <c>ruleId + eventId</c> ile idempotent çalışır).
/// </summary>
public sealed record DealStageChangedIntegration(
    Guid TenantId,
    Guid DealId,
    Guid PipelineId,
    Guid? FromStageId,
    Guid ToStageId,
    string ToStageKind,
    decimal? Amount,
    string Currency,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>
/// Lead sahipliği sözleşmesi (Sales uygular): workflow'un round-robin atamasını Sales verisi üzerinde yapabilmesi için.
/// Aktif kiracı bağlamında çalışır.
/// </summary>
public interface ILeadOwnerService
{
    /// <summary>
    /// Lead'in sahibini değiştirir (son atama zamanı güncellenir). Lead yok/silinmiş/başka kiracı → <c>not_found</c>;
    /// dönüşmüş lead → <c>lead.already_converted</c>; hedef aktif üye değil → <c>owner.not_member</c>.
    /// </summary>
    Task<Result> AssignOwnerAsync(Guid leadId, Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>Kullanıcı başına açık lead sayısı (<c>new|contacted|qualified</c>; silinmiş, dönüşmüş ve nitelikli-olmayan hariç). Kaydı olmayan kullanıcı 0 döner.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountOpenLeadsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kullanıcıya en son lead atanma zamanı (UTC): kullanıcının mevcut lead'leri arasında en yeni sahiplik anı
    /// (atama yapılmamışsa oluşturulma anı). Hiç lead'i olmayan kullanıcı sonuçta yer almaz.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DateTime>> GetLastAssignedAtAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
}
