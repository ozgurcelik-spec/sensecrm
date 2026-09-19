using Sense.Crm.Modules.Sales.Domain.Accounts;
using Sense.Crm.Modules.Sales.Domain.Contacts;
using Sense.Crm.Modules.Sales.Domain.Deals;
using Sense.Crm.Modules.Sales.Domain.Leads;
using Sense.Crm.Modules.Sales.Domain.Pipelines;

namespace Sense.Crm.Modules.Sales.Domain;

// Depo arayüzleri komut tarafıdır (agregat yükle/ekle/sil). Tüm sorgular kiracı ve yumuşak silme filtresi altında çalışır:
// başka organizasyonun kaydı burada hiç bulunmaz (null → not_found).

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Firmaya bağlı (silinmemiş) kişi veya fırsat var mı.</summary>
    Task<bool> HasDependentsAsync(Guid accountId, CancellationToken ct);

    void Add(Account account);

    void Remove(Account account);
}

public interface IContactRepository
{
    Task<Contact?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(Contact contact);

    void Remove(Contact contact);
}

public interface ILeadRepository
{
    Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(Lead lead);

    void Remove(Lead lead);
}

public interface IPipelineRepository
{
    /// <summary>Aşamalarıyla birlikte yükler.</summary>
    Task<Pipeline?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Varsayılan huni (aşamalarıyla); organizasyonda yoksa null.</summary>
    Task<Pipeline?> GetDefaultAsync(CancellationToken ct);

    Task<bool> AnyAsync(CancellationToken ct);

    Task<PipelineStage?> GetStageAsync(Guid stageId, CancellationToken ct);

    /// <summary>Huninin aşamalarından, üzerinde (silinmemiş) fırsat olanların kimlikleri.</summary>
    Task<IReadOnlySet<Guid>> GetStageIdsInUseAsync(Guid pipelineId, CancellationToken ct);

    void Add(Pipeline pipeline);

    void RemoveStages(IEnumerable<PipelineStage> stages);
}

public interface IDealRepository
{
    Task<Deal?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(Deal deal);

    void Remove(Deal deal);
}
