using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Kernel.Time;

namespace Sense.Crm.Modules.Identity.Contracts;

/// <summary>Çözülmüş rapor aralığı: kiracı takvimi, yerel gün sınırları ve bunların UTC yarı açık karşılığı.</summary>
public sealed record ResolvedReportRange(TenantCalendar Calendar, DateOnly From, DateOnly To, DateTime FromUtc, DateTime ToExclusiveUtc);

/// <summary>
/// Modüller arası kiracı takvimi yardımcısı: aktif organizasyonun saat dilimini <see cref="ITenantDirectory"/>'den okuyup
/// "bugün / hafta başı / rapor aralığı" hesaplarını kiracı saat diliminde yapar (rapor ve özet sınırları UTC değildir).
/// Identity modülü kaydeder; Sales ve Activities kullanır.
/// </summary>
public sealed class TenantCalendarService(ITenantDirectory directory, ITenantContext tenant, TimeProvider clock)
{
    /// <summary>Aktif organizasyonun takvimi; organizasyon/saat dilimi bulunamazsa UTC.</summary>
    public async Task<TenantCalendar> GetCalendarAsync(CancellationToken cancellationToken = default)
    {
        var info = await directory.FindAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        return TenantCalendar.For(info?.TimeZone);
    }

    public DateTimeOffset UtcNow => clock.GetUtcNow();

    /// <summary>
    /// <c>from</c>/<c>to</c> (uçlar dahil, kiracı saat diliminde takvim günü) → UTC sınırları. Verilmezse son 12 ay
    /// (<see cref="TenantCalendar.ResolveReportRange"/>). Ters aralık <c>validation</c> döner.
    /// </summary>
    public async Task<Result<ResolvedReportRange>> ResolveReportRangeAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var calendar = await GetCalendarAsync(cancellationToken).ConfigureAwait(false);
        var (start, end) = TenantCalendar.ResolveReportRange(from, to, calendar.Today(clock.GetUtcNow()));
        if (end < start)
        {
            return Error.Validation(ErrorCodes.ValidationError);
        }

        var (fromUtc, toExclusiveUtc) = calendar.UtcBounds(start, end);
        return new ResolvedReportRange(calendar, start, end, fromUtc, toExclusiveUtc);
    }
}
