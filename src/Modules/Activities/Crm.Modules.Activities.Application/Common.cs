using Crm.Modules.Activities.Domain;
using Crm.Modules.Activities.Domain.Activities;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Contracts;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Kernel.Results;
using Crm.Shared.Kernel.Time;

namespace Crm.Modules.Activities.Application;

/// <summary>
/// Atanan kullanıcı kuralı: verilmezse çağıran kullanıcı; verilen (ve mevcut atanandan farklı) kullanıcı aktif organizasyonun
/// aktif üyesi olmalıdır (<c>owner.not_member</c>). Mevcut atanan korunurken üyelik yeniden sorgulanmaz.
/// </summary>
public sealed class AssigneeResolver(IMemberLookup members, ICurrentUser user)
{
    public async Task<Result<Guid>> ResolveAsync(Guid? requested, Guid? current, CancellationToken ct)
    {
        var target = requested ?? current ?? user.UserId;
        if (target is not { } assignee)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        if (requested is { } candidate && candidate != current && !await members.IsActiveMemberAsync(candidate, ct).ConfigureAwait(false))
        {
            return Error.Validation(ActivitiesErrors.OwnerNotMember);
        }

        return assignee;
    }
}

/// <summary>
/// İlişkili kaydın aktif organizasyonda var olduğunu <see cref="IRecordLookup"/> ile doğrular (başka organizasyonun veya silinmiş
/// kayıt bulunamaz → <c>activity.related_not_found</c>, 404). Yalnız <c>Sales.Contracts</c> üzerinden konuşur.
/// </summary>
public sealed class RelatedRecordVerifier(IRecordLookup records)
{
    public async Task<Result> VerifyAsync(ActivityRelatedType? type, Guid? id, CancellationToken ct)
    {
        if (type is null || id is null)
        {
            return Result.Success();
        }

        return await records.ExistsAsync(ToRecordType(type.Value), id.Value, ct).ConfigureAwait(false)
            ? Result.Success()
            : Error.NotFound(ActivitiesErrors.RelatedNotFound);
    }

    public static RecordType ToRecordType(ActivityRelatedType type) => type switch
    {
        ActivityRelatedType.Account => RecordType.Account,
        ActivityRelatedType.Contact => RecordType.Contact,
        ActivityRelatedType.Lead => RecordType.Lead,
        ActivityRelatedType.Deal => RecordType.Deal,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

/// <summary>Özet penceresi: kiracı saat dilimine göre "bugün" ve hafta başı (pazartesi), UTC sınırlarıyla (saf hesap).</summary>
public static class ActivitySummaryWindows
{
    public static ActivitySummaryWindow For(TenantCalendar calendar, DateTimeOffset nowUtc)
    {
        var today = calendar.Today(nowUtc);
        var weekStart = TenantCalendar.StartOfWeek(today);
        return new ActivitySummaryWindow(
            nowUtc.UtcDateTime,
            calendar.StartOfDayUtc(today),
            calendar.StartOfDayUtc(today.AddDays(1)),
            calendar.StartOfDayUtc(weekStart),
            calendar.StartOfDayUtc(weekStart.AddDays(7)));
    }
}
