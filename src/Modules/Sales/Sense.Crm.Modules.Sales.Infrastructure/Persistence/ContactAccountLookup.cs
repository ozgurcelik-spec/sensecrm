using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Sales.Contracts;

namespace Sense.Crm.Modules.Sales.Infrastructure.Persistence;

/// <summary><see cref="IContactAccountLookup"/> uygulaması: <c>SalesDbContext</c>'in kiracı + yumuşak silme filtresi altında tek sorgu.</summary>
public sealed class ContactAccountLookup(SalesDbContext db) : IContactAccountLookup
{
    public async Task<ContactAccountLink?> FindAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        await db.Contacts.AsNoTracking()
            .Where(c => c.Id == contactId)
            .Select(c => new ContactAccountLink(c.Id, c.AccountId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
