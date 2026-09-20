using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Files;

namespace Sense.Crm.Modules.Sales.Infrastructure;

/// <summary>
/// Sales'in dosya eki hedefleri (M8C): firma, kişi, potansiyel müşteri, fırsat. <b>Kiracı + yumuşak silme filtresi altında</b> tek sorgulukla verilen kimliklerden var olanları döner
/// (başka kiracı/silinmiş kayıt asla dönmez). İzin anahtarları <see cref="SalesPermissions"/> sabitlerindendir (dizge kopyalanmaz).
/// </summary>
internal abstract class SalesAttachmentTarget(string recordType, string readPermission, string writePermission) : IAttachmentTarget
{
    public string RecordType { get; } = recordType;

    public string Module => SalesDbContext.SchemaName;

    public string ReadPermission { get; } = readPermission;

    public string WritePermission { get; } = writePermission;

    public async Task<IReadOnlySet<Guid>> GetExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var array = ids as Guid[] ?? ids.ToArray();
        return (await ExistingAsync(array, ct).ConfigureAwait(false)).ToHashSet();
    }

    protected abstract Task<List<Guid>> ExistingAsync(Guid[] ids, CancellationToken ct);
}

internal sealed class AccountAttachmentTarget(SalesDbContext db)
    : SalesAttachmentTarget(AttachmentRecordTypes.Account, SalesPermissions.AccountsRead, SalesPermissions.AccountsWrite)
{
    protected override Task<List<Guid>> ExistingAsync(Guid[] ids, CancellationToken ct) =>
        db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);
}

internal sealed class ContactAttachmentTarget(SalesDbContext db)
    : SalesAttachmentTarget(AttachmentRecordTypes.Contact, SalesPermissions.ContactsRead, SalesPermissions.ContactsWrite)
{
    protected override Task<List<Guid>> ExistingAsync(Guid[] ids, CancellationToken ct) =>
        db.Contacts.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
}

internal sealed class LeadAttachmentTarget(SalesDbContext db)
    : SalesAttachmentTarget(AttachmentRecordTypes.Lead, SalesPermissions.LeadsRead, SalesPermissions.LeadsWrite)
{
    protected override Task<List<Guid>> ExistingAsync(Guid[] ids, CancellationToken ct) =>
        db.Leads.AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct);
}

internal sealed class DealAttachmentTarget(SalesDbContext db)
    : SalesAttachmentTarget(AttachmentRecordTypes.Deal, SalesPermissions.DealsRead, SalesPermissions.DealsWrite)
{
    protected override Task<List<Guid>> ExistingAsync(Guid[] ids, CancellationToken ct) =>
        db.Deals.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct);
}
