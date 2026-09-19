using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Shared.Kernel.ValueObjects;

/// <summary>Kapalı gün aralığı [Start, End].</summary>
public sealed class DateRange : ValueObject
{
    private DateRange(DateOnly start, DateOnly end)
    {
        Start = start;
        End = end;
    }

    public DateOnly Start { get; }

    public DateOnly End { get; }

    public static DateRange Of(DateOnly start, DateOnly end)
    {
        Guard.Against(end < start, KernelMessages.EndBeforeStart);
        return new DateRange(start, end);
    }

    public static DateRange SingleDay(DateOnly day) => new(day, day);

    /// <summary>Aralıktaki takvim günü sayısı (her iki uç dahil).</summary>
    public int CalendarDays => End.DayNumber - Start.DayNumber + 1;

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    public bool Overlaps(DateRange other) => Start <= other.End && other.Start <= End;

    public DateRange? Intersect(DateRange other)
    {
        if (!Overlaps(other))
        {
            return null;
        }

        var s = Start > other.Start ? Start : other.Start;
        var e = End < other.End ? End : other.End;
        return new DateRange(s, e);
    }

    public IEnumerable<DateOnly> Days()
    {
        for (var d = Start; d <= End; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    public override string ToString() => $"{Start:yyyy-MM-dd}..{End:yyyy-MM-dd}";

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Start;
        yield return End;
    }
}
