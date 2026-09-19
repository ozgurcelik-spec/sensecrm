using Crm.Modules.Service.Domain.Cases;
using Crm.Modules.Service.Domain.Sla;

namespace Crm.Modules.Service.Domain;

// Depo arayüzleri komut tarafıdır (agregat yükle/ekle/sil). Sorgular kiracı ve yumuşak silme filtresi altında çalışır:
// başka organizasyonun kaydı burada hiç bulunmaz (null → not_found).
public interface ICaseRepository
{
    Task<Case?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(Case entity);

    void Remove(Case entity);

    void AddEvent(CaseEvent caseEvent);

    void AddComment(CaseComment comment);

    /// <summary>
    /// Yorum işleyicisinin xmin çakışmasında tek sefer yeniden deneyebilmesi için ara kaydetme (komut transaction'ının içinde;
    /// başarısız kayıt EF'in otomatik savepoint'ine geri alınır). Diğer işleyiciler kaydı UnitOfWork davranışına bırakır.
    /// </summary>
    /// <returns>Kaydedildiyse true; eşzamanlılık (xmin) çakışmasında izlenen varlıklar bırakılır ve false döner (yeniden yükleyip deneyin).</returns>
    Task<bool> TrySaveAsync(CancellationToken ct);
}

public interface ISlaPolicyRepository
{
    /// <summary>Kiracının politikaları (öncelik sırasıyla).</summary>
    Task<IReadOnlyList<SlaPolicy>> ListAsync(CancellationToken ct);

    Task<SlaPolicy?> GetAsync(CasePriority priority, CancellationToken ct);
}
