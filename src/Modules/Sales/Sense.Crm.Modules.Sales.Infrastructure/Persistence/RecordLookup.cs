using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Sales.Contracts;

namespace Sense.Crm.Modules.Sales.Infrastructure.Persistence;

/// <summary>
/// <see cref="IRecordLookup"/> uygulaması: tüm sorgular <c>SalesDbContext</c>'in kiracı + yumuşak silme filtresi altındadır.
/// Toplu çözüm tür başına tek sorgudur (N+1 yok).
/// </summary>
public sealed class RecordLookup(SalesDbContext db) : IRecordLookup
{
    public async Task<bool> ExistsAsync(RecordType type, Guid id, CancellationToken cancellationToken = default) => type switch
    {
        RecordType.Account => await db.Accounts.AsNoTracking().AnyAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false),
        RecordType.Contact => await db.Contacts.AsNoTracking().AnyAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false),
        RecordType.Lead => await db.Leads.AsNoTracking().AnyAsync(l => l.Id == id, cancellationToken).ConfigureAwait(false),
        RecordType.Deal => await db.Deals.AsNoTracking().AnyAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false),
        _ => false,
    };

    public async Task<Guid?> GetOwnerUserIdAsync(RecordType type, Guid id, CancellationToken cancellationToken = default) => type switch
    {
        RecordType.Account => await db.Accounts.AsNoTracking().Where(a => a.Id == id).Select(a => (Guid?)a.OwnerUserId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
        RecordType.Contact => await db.Contacts.AsNoTracking().Where(c => c.Id == id).Select(c => (Guid?)c.OwnerUserId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
        RecordType.Lead => await db.Leads.AsNoTracking().Where(l => l.Id == id).Select(l => (Guid?)l.OwnerUserId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
        RecordType.Deal => await db.Deals.AsNoTracking().Where(d => d.Id == id).Select(d => (Guid?)d.OwnerUserId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
        _ => null,
    };

    public async Task<string?> GetDisplayNameAsync(RecordType type, Guid id, CancellationToken cancellationToken = default)
    {
        var names = await GetDisplayNamesAsync([new RecordRef(type, id)], cancellationToken).ConfigureAwait(false);
        return names.TryGetValue(new RecordRef(type, id), out var name) ? name : null;
    }

    public async Task<IReadOnlyDictionary<RecordRef, string>> GetDisplayNamesAsync(IReadOnlyCollection<RecordRef> records, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<RecordRef, string>();
        foreach (var group in records.GroupBy(r => r.Type))
        {
            var ids = group.Select(r => r.Id).Distinct().ToArray();
            foreach (var (id, name) in await NamesAsync(group.Key, ids, cancellationToken).ConfigureAwait(false))
            {
                result[new RecordRef(group.Key, id)] = name;
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<(Guid Id, string Name)>> NamesAsync(RecordType type, Guid[] ids, CancellationToken ct)
    {
        switch (type)
        {
            case RecordType.Account:
                return (await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Name }).ToListAsync(ct).ConfigureAwait(false))
                    .Select(a => (a.Id, a.Name)).ToList();

            case RecordType.Deal:
                return (await db.Deals.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.Name }).ToListAsync(ct).ConfigureAwait(false))
                    .Select(d => (d.Id, d.Name)).ToList();

            case RecordType.Contact:
                return (await db.Contacts.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.FirstName, c.LastName }).ToListAsync(ct).ConfigureAwait(false))
                    .Select(c => (c.Id, FullName(c.FirstName, c.LastName))).ToList();

            case RecordType.Lead:
                return (await db.Leads.AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => new { l.Id, l.FirstName, l.LastName }).ToListAsync(ct).ConfigureAwait(false))
                    .Select(l => (l.Id, FullName(l.FirstName, l.LastName))).ToList();

            default:
                return [];
        }
    }

    private static string FullName(string? firstName, string lastName) =>
        string.Join(' ', new[] { firstName, lastName }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// <see cref="IRecordRelationLookup"/> uygulaması: kiracı + yumuşak silme filtresi altında, yalnız gereken sütunları okur.
/// </summary>
public sealed class RecordRelationLookup(SalesDbContext db) : IRecordRelationLookup
{
    public async Task<ContactLink?> GetContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await db.Contacts.AsNoTracking()
            .Where(c => c.Id == contactId)
            .Select(c => new { c.Id, c.FirstName, c.LastName, c.AccountId })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return contact is null
            ? null
            : new ContactLink(contact.Id, string.Join(' ', new[] { contact.FirstName, contact.LastName }.Where(p => !string.IsNullOrWhiteSpace(p))), contact.AccountId);
    }

    public async Task<DealLink?> GetDealAsync(Guid dealId, CancellationToken cancellationToken = default)
    {
        var deal = await db.Deals.AsNoTracking()
            .Where(d => d.Id == dealId)
            .Select(d => new { d.Id, d.Name, d.AccountId, d.ContactId, d.Currency })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return deal is null ? null : new DealLink(deal.Id, deal.Name, deal.AccountId, deal.ContactId, deal.Currency);
    }
}
