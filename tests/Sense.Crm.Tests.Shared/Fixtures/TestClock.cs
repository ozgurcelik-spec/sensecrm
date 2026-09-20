namespace Sense.Crm.Tests.Shared.Fixtures;

/// <summary>Elle yönetilen saat: <see cref="SetUtcNow"/> ileri VE geri alınabilir (FakeTimeProvider geri almayı reddeder). Yaşam döngüsü/deneme bitişi/imha zamanlaması testleri için.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void SetUtcNow(DateTimeOffset value) => _now = value;

    public void Advance(TimeSpan delta) => _now += delta;
}
