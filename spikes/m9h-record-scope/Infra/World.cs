using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Model;

namespace Sense.Crm.Spikes.M9h.Infra;

/// <summary>
/// Plan fixture'ının küçültülmüşü: <c>Boss ← M1 ← {R1, R2}</c>, <c>Boss ← M2 ← {R3}</c>, bağımsız <c>X</c>; her kullanıcıya iki fırsat/firma (+ her firmaya bir kişi),
/// bir atanmış + bir sahipsiz talep (+ yorumlar), silinmiş bir fırsat; ayrıca kiracı T2'de AYNI kullanıcı kimlikleriyle simetrik veri.
/// </summary>
public sealed class World
{
    public Guid T1 { get; } = Guid.NewGuid();

    public Guid T2 { get; } = Guid.NewGuid();

    public Guid Boss { get; } = Guid.NewGuid();

    public Guid M1 { get; } = Guid.NewGuid();

    public Guid M2 { get; } = Guid.NewGuid();

    public Guid R1 { get; } = Guid.NewGuid();

    public Guid R2 { get; } = Guid.NewGuid();

    public Guid R3 { get; } = Guid.NewGuid();

    public Guid X { get; } = Guid.NewGuid();

    public Guid[] AllUsers => [Boss, M1, M2, R1, R2, R3, X];

    /// <summary>Boss altındaki tüm astlar (dolaylı dahil).</summary>
    public Guid[] BossSubordinates => [M1, M2, R1, R2, R3];

    public Guid[] M1Subordinates => [R1, R2];

    public Dictionary<(Guid Tenant, Guid Owner), List<Guid>> DealIds { get; } = [];

    public Guid DeletedDealOfR1 { get; private set; }

    public Guid PoolCase { get; private set; }

    public Guid CrossCase { get; private set; }

    public static RecordScopeSnapshot Snap(string resourceOrAll, ResourceScope scope, Guid user) =>
        new(user, false, new Dictionary<string, ResourceScope>
        {
            ["deal"] = scope,
            ["account"] = scope,
            ["contact"] = scope,
            ["case"] = scope,
        });

    public static RecordScopeSnapshot Own(Guid user) => Snap("*", ResourceScope.Own(user), user);

    public static RecordScopeSnapshot OwnAndSubs(Guid user, IEnumerable<Guid> subs, bool includeUnowned = false) =>
        Snap("*", ResourceScope.OwnAnd([user, .. subs], includeUnowned), user);

    public static RecordScopeSnapshot Admin(Guid user) => new(user, true, new Dictionary<string, ResourceScope>());

    public async Task SeedAsync(PgFixture pg)
    {
        foreach (var tenant in new[] { T1, T2 })
        {
            using var _ = new TenantContext().BeginScope(tenant);
            await using var db = pg.CreateMember();
            foreach (var user in AllUsers)
            {
                var deals = new List<Guid>();
                for (var i = 0; i < 2; i++)
                {
                    var d = new SDeal { Id = Guid.NewGuid(), TenantId = tenant, OwnerUserId = user, Name = $"deal-{i}", Amount = 100 * (i + 1), CreatedAt = DateTime.UtcNow };
                    db.Deals.Add(d);
                    deals.Add(d.Id);
                }

                DealIds[(tenant, user)] = deals;

                var acc = new SAccount { Id = Guid.NewGuid(), TenantId = tenant, OwnerUserId = user, Name = "acc" };
                acc.Contacts.Add(new SContact { Id = Guid.NewGuid(), TenantId = tenant, OwnerUserId = user, AccountId = acc.Id, Name = "own-contact" });
                db.Accounts.Add(acc);

                var assigned = new SCase { Id = Guid.NewGuid(), TenantId = tenant, AssignedUserId = user, CreatedUserId = user, Title = "assigned" };
                assigned.Comments.Add(new SCaseComment { Id = Guid.NewGuid(), TenantId = tenant, CaseId = assigned.Id, Body = "c1" });
                db.Cases.Add(assigned);
            }

            // Sahipsiz havuz talebi (kimse atanmamış, oluşturan X)
            var pool = new SCase { Id = Guid.NewGuid(), TenantId = tenant, AssignedUserId = null, CreatedUserId = X, Title = "pool" };
            pool.Comments.Add(new SCaseComment { Id = Guid.NewGuid(), TenantId = tenant, CaseId = pool.Id, Body = "pool-c" });
            db.Cases.Add(pool);
            if (tenant == T1)
            {
                PoolCase = pool.Id;
            }

            // Çift yol: R3'e atanmış, X tarafından oluşturulmuş talep (atanan VEYA oluşturan görür).
            var cross = new SCase { Id = Guid.NewGuid(), TenantId = tenant, AssignedUserId = R3, CreatedUserId = X, Title = "cross" };
            db.Cases.Add(cross);
            if (tenant == T1)
            {
                CrossCase = cross.Id;
            }

            var deleted = new SDeal { Id = Guid.NewGuid(), TenantId = tenant, OwnerUserId = R1, Name = "deleted", IsDeleted = true };
            db.Deals.Add(deleted);
            if (tenant == T1)
            {
                DeletedDealOfR1 = deleted.Id;
            }

            db.Products.Add(new SProduct { Id = Guid.NewGuid(), TenantId = tenant, Name = "prod" });
            await db.SaveChangesAsync();
        }
    }
}
