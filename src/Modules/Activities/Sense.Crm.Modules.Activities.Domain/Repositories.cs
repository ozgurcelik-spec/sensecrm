using Sense.Crm.Modules.Activities.Domain.Activities;

namespace Sense.Crm.Modules.Activities.Domain;

// Depo arayüzü komut tarafıdır (agregat yükle/ekle/sil). Sorgular kiracı ve yumuşak silme filtresi altında çalışır:
// başka organizasyonun kaydı burada hiç bulunmaz (null → not_found).
public interface IActivityRepository
{
    Task<Activity?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(Activity activity);

    void Remove(Activity activity);
}
