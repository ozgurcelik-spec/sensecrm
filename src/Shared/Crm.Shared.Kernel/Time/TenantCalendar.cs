using System.Globalization;

namespace Crm.Shared.Kernel.Time;

/// <summary>
/// Organizasyonun saat dilimine göre takvim hesapları: "bugün", hafta başı (pazartesi), gün başlangıcı ve yerel tarih
/// aralıklarının UTC sınırları. Saklanan tüm zamanlar UTC'dir; kullanıcıya dönük "bugün / bu hafta / bu ay" kavramları
/// kiracının saat diliminde hesaplanır (Milestone 3 kararı: rapor ve özet sınırları UTC değil, kiracı saat dilimidir).
/// Bilinmeyen saat dilimi kimliği UTC'ye düşer (raporlar yine de çalışır).
/// </summary>
public sealed class TenantCalendar
{
    public const string UtcId = "UTC";
    private const int MonthsInDefaultReportRange = 12;
    private const int InvalidTimeStepMinutes = 30;

    private readonly TimeZoneInfo _zone;

    private TenantCalendar(TimeZoneInfo zone, string ianaId)
    {
        _zone = zone;
        IanaId = ianaId;
    }

    /// <summary>PostgreSQL <c>AT TIME ZONE</c> ile de kullanılabilen IANA kimliği ("Europe/Istanbul", yoksa "UTC").</summary>
    public string IanaId { get; }

    public static TenantCalendar Utc { get; } = new(TimeZoneInfo.Utc, UtcId);

    /// <summary>IANA (veya işletim sistemi) saat dilimi kimliğinden takvim üretir; boş/bilinmeyen kimlik → UTC.</summary>
    public static TenantCalendar For(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return Utc;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            if (zone.HasIanaId)
            {
                return new TenantCalendar(zone, zone.Id);
            }

            return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && !string.IsNullOrEmpty(iana)
                ? new TenantCalendar(zone, iana)
                : Utc;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Utc;
        }
    }

    /// <summary>Kiracının yerel takvim günü.</summary>
    public DateOnly Today(DateTimeOffset nowUtc) => LocalDate(nowUtc.UtcDateTime);

    /// <summary>UTC anının kiracı saat dilimindeki takvim tarihi.</summary>
    public DateOnly LocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(utc), _zone));

    /// <summary>Yerel günün 00:00'ının UTC karşılığı (yaz saati boşluğunda ilk geçerli an; belirsiz saatte erken olan).</summary>
    public DateTime StartOfDayUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (_zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(InvalidTimeStepMinutes);
        }

        if (_zone.IsAmbiguousTime(local))
        {
            var offset = _zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, _zone);
    }

    /// <summary>Verilen günün ait olduğu haftanın pazartesisi.</summary>
    public static DateOnly StartOfWeek(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    /// <summary>Kapalı yerel tarih aralığı [from, to] → UTC yarı açık aralık [FromUtc, ToExclusiveUtc).</summary>
    public (DateTime FromUtc, DateTime ToExclusiveUtc) UtcBounds(DateOnly from, DateOnly to) =>
        (StartOfDayUtc(from), StartOfDayUtc(to.AddDays(1)));

    /// <summary>
    /// Rapor aralığı varsayılanı ("son 12 ay"): <c>to</c> = bugün, <c>from</c> = 11 ay önceki ayın ilk günü
    /// (ay bazlı gruplamada tam 12 dönem). Verilen uçlar korunur.
    /// </summary>
    public static (DateOnly From, DateOnly To) ResolveReportRange(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1).AddMonths(1 - MonthsInDefaultReportRange);
        return (start, end);
    }

    /// <summary>ISO 8601 hafta etiketi: "2026-W38".</summary>
    public static string IsoWeekLabel(DateOnly date)
    {
        var dateTime = date.ToDateTime(TimeOnly.MinValue);
        return string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(dateTime):D4}-W{ISOWeek.GetWeekOfYear(dateTime):D2}");
    }

    /// <summary>Ay etiketi: "2026-09".</summary>
    public static string MonthLabel(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"{date.Year:D4}-{date.Month:D2}");

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
