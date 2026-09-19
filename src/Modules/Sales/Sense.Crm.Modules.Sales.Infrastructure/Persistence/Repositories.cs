using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Sales.Domain;
using Sense.Crm.Modules.Sales.Domain.Accounts;
using Sense.Crm.Modules.Sales.Domain.Contacts;
using Sense.Crm.Modules.Sales.Domain.Deals;
using Sense.Crm.Modules.Sales.Domain.Leads;
using Sense.Crm.Modules.Sales.Domain.Pipelines;

namespace Sense.Crm.Modules.Sales.Infrastructure.Persistence;

// Tüm sorgular ModuleDbContext'in "Tenant" ve "SoftDelete" global filtreleri altında çalışır.

public sealed class AccountRepository(SalesDbContext db) : IAccountRepository
{
    public Task<Account?> GetByIdAsync(Guid id, CancellationToken ct) => db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<bool> HasDependentsAsync(Guid accountId, CancellationToken ct) =>
        await db.Contacts.AnyAsync(c => c.AccountId == accountId, ct) || await db.Deals.AnyAsync(d => d.AccountId == accountId, ct);

    public void Add(Account account) => db.Accounts.Add(account);

    public void Remove(Account account) => db.Accounts.Remove(account);
}

public sealed class ContactRepository(SalesDbContext db) : IContactRepository
{
    public Task<Contact?> GetByIdAsync(Guid id, CancellationToken ct) => db.Contacts.FirstOrDefaultAsync(c => c.Id == id, ct);

    public void Add(Contact contact) => db.Contacts.Add(contact);

    public void Remove(Contact contact) => db.Contacts.Remove(contact);
}

public sealed class LeadRepository(SalesDbContext db) : ILeadRepository
{
    public Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct) => db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);

    public void Add(Lead lead) => db.Leads.Add(lead);

    public void Remove(Lead lead) => db.Leads.Remove(lead);
}

public sealed class PipelineRepository(SalesDbContext db) : IPipelineRepository
{
    public Task<Pipeline?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Pipelines.Include(p => p.Stages).FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Pipeline?> GetDefaultAsync(CancellationToken ct) =>
        db.Pipelines.Include(p => p.Stages).Where(p => p.IsDefault).OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(ct);

    public Task<bool> AnyAsync(CancellationToken ct) => db.Pipelines.AnyAsync(ct);

    public Task<PipelineStage?> GetStageAsync(Guid stageId, CancellationToken ct) => db.PipelineStages.FirstOrDefaultAsync(s => s.Id == stageId, ct);

    public async Task<IReadOnlySet<Guid>> GetStageIdsInUseAsync(Guid pipelineId, CancellationToken ct) =>
        (await db.Deals.Where(d => d.PipelineId == pipelineId).Select(d => d.StageId).Distinct().ToListAsync(ct)).ToHashSet();

    public void Add(Pipeline pipeline) => db.Pipelines.Add(pipeline);

    public void RemoveStages(IEnumerable<PipelineStage> stages) => db.PipelineStages.RemoveRange(stages);
}

public sealed class DealRepository(SalesDbContext db) : IDealRepository
{
    public Task<Deal?> GetByIdAsync(Guid id, CancellationToken ct) => db.Deals.FirstOrDefaultAsync(d => d.Id == id, ct);

    public void Add(Deal deal) => db.Deals.Add(deal);

    public void Remove(Deal deal) => db.Deals.Remove(deal);
}
