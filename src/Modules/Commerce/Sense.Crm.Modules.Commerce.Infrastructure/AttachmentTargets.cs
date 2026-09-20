using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Files;

namespace Sense.Crm.Modules.Commerce.Infrastructure;

/// <summary>
/// Commerce'in dosya eki hedefleri (M8C): teklif ve satış siparişi. Kiracı + yumuşak silme filtresi altında tek sorgulukla var olanları döner; izin anahtarları
/// <see cref="CommercePermissions"/> sabitlerindendir. Kapı modülü <c>commerce</c> (plan kapalıysa dosya uçları <c>403 plan.module_disabled</c>).
/// </summary>
internal abstract class CommerceAttachmentTarget(string recordType, string readPermission, string writePermission) : IAttachmentTarget
{
    public string RecordType { get; } = recordType;

    public string Module => CommerceDbContext.SchemaName;

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

internal sealed class QuoteAttachmentTarget(CommerceDbContext db)
    : CommerceAttachmentTarget(AttachmentRecordTypes.Quote, CommercePermissions.QuotesRead, CommercePermissions.QuotesWrite)
{
    protected override Task<List<Guid>> ExistingAsync(Guid[] ids, CancellationToken ct) =>
        db.Quotes.AsNoTracking().Where(q => ids.Contains(q.Id)).Select(q => q.Id).ToListAsync(ct);
}

internal sealed class OrderAttachmentTarget(CommerceDbContext db)
    : CommerceAttachmentTarget(AttachmentRecordTypes.Order, CommercePermissions.OrdersRead, CommercePermissions.OrdersWrite)
{
    protected override Task<List<Guid>> ExistingAsync(Guid[] ids, CancellationToken ct) =>
        db.SalesOrders.AsNoTracking().Where(o => ids.Contains(o.Id)).Select(o => o.Id).ToListAsync(ct);
}
