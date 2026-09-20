namespace Sense.Crm.Modules.Platform.Domain.Usage;

/// <summary>
/// Günlük kullanım anlık görüntüsü (UTC günü): kullanıcı sayıları kolonlarda, diğer metrikler <c>metrics jsonb</c>'de
/// (<c>{"sales.accounts": 120, "sales.records": 340, …}</c>; yeni metrik göç gerektirmez). PK <c>(tenant_id, day)</c> (kiracı önce). Küresel
/// tablodur (<c>ITenantEntity</c> DEĞİL); yazma idempotent upsert'tir (günde birden çok çalışma tek satır).
/// </summary>
public sealed class UsageSnapshot
{
    public Guid TenantId { get; set; }

    public DateOnly Day { get; set; }

    public int UsersActive { get; set; }

    public int UsersPending { get; set; }

    public IReadOnlyDictionary<string, long> Metrics { get; set; } = new Dictionary<string, long>();

    public DateTime TakenAt { get; set; }
}
