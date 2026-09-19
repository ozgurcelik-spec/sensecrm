using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Activities.Contracts;

/// <summary>
/// Modüller arası aktivite oluşturma sözleşmesi (Activities uygular): workflow'un görev/not açması için. Aktif kiracı
/// bağlamında çalışır; atanan aktif üye olmalıdır (<c>owner.not_member</c>), ilişkili kayıt aktif organizasyonda bulunmalıdır
/// (<c>activity.related_not_found</c>). Oluşan aktivitenin kimliğini döner.
/// </summary>
public interface IActivityCreator
{
    /// <summary>Açık bir görev oluşturur (<paramref name="related"/> kaydına bağlı, <paramref name="assigneeUserId"/>'ye atanmış).</summary>
    Task<Result<Guid>> CreateTaskAsync(
        Guid assigneeUserId,
        string subject,
        string? description,
        DateTime? dueAt,
        RecordRef related,
        CancellationToken cancellationToken = default);

    /// <summary>Kayda bağlı bir not oluşturur (notlar her zaman tamamlanmış durumdadır).</summary>
    Task<Result<Guid>> CreateNoteAsync(
        Guid assigneeUserId,
        string subject,
        string? description,
        RecordRef related,
        CancellationToken cancellationToken = default);
}
