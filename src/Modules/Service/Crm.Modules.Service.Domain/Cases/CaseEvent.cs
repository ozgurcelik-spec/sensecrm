using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;

namespace Crm.Modules.Service.Domain.Cases;

/// <summary>
/// Zaman çizelgesi olayı: yalnız ekleme, değişmez. <see cref="FromValue"/>/<see cref="ToValue"/> durum/öncelik camelCase metni,
/// atamada kullanıcı kimliği metnidir (atanmamış = boş). <see cref="Note"/> çözüm/kapatma notudur. Denetim dışıdır
/// (kendisi değişmez bir olay günlüğü; çift yazımı önlemek için) — Lead onaylı bilinçli istisna.
/// </summary>
public sealed class CaseEvent : TenantEntity<Guid>
{
    private CaseEvent()
    {
    }

    private CaseEvent(Guid id, Guid tenantId, Guid caseId, CaseEventType type, Guid? actorUserId, string? fromValue, string? toValue, string? note)
        : base(id, tenantId)
    {
        CaseId = caseId;
        Type = type;
        ActorUserId = actorUserId;
        FromValue = fromValue;
        ToValue = toValue;
        Note = note;
    }

    public Guid CaseId { get; private set; }

    public CaseEventType Type { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public string? FromValue { get; private set; }

    public string? ToValue { get; private set; }

    public string? Note { get; private set; }

    public static CaseEvent Create(Guid tenantId, Guid caseId, CaseEventType type, Guid? actorUserId, string? fromValue = null, string? toValue = null, string? note = null) =>
        new(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.NotDefault(caseId),
            type,
            actorUserId,
            Clean(fromValue, ServiceLimits.EventValueMaxLength),
            Clean(toValue, ServiceLimits.EventValueMaxLength),
            Clean(note, ServiceLimits.ResolutionNoteMaxLength));

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, maxLength);
    }
}
