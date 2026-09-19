using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Activities.Domain;
using Sense.Crm.Modules.Activities.Domain.Activities;

namespace Sense.Crm.Modules.Activities.Infrastructure.Persistence;

// Tüm sorgular ModuleDbContext'in "Tenant" ve "SoftDelete" global filtreleri altında çalışır.

public sealed class ActivityRepository(ActivitiesDbContext db) : IActivityRepository
{
    public Task<Activity?> GetByIdAsync(Guid id, CancellationToken ct) => db.Activities.FirstOrDefaultAsync(a => a.Id == id, ct);

    public void Add(Activity activity) => db.Activities.Add(activity);

    public void Remove(Activity activity) => db.Activities.Remove(activity);
}
