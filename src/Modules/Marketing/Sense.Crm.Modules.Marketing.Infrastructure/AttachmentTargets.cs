using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Marketing.Contracts;
using Sense.Crm.Modules.Marketing.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Files;

namespace Sense.Crm.Modules.Marketing.Infrastructure;

/// <summary>
/// Marketing'in dosya eki hedefi (M8C): kampanya. Kiracı + yumuşak silme filtresi altında tek sorgulukla var olanları döner; izin anahtarları
/// <see cref="MarketingPermissions"/> sabitlerindendir. Kapı modülü <c>marketing</c>.
/// </summary>
internal sealed class CampaignAttachmentTarget(MarketingDbContext db) : IAttachmentTarget
{
    public string RecordType => AttachmentRecordTypes.Campaign;

    public string Module => MarketingDbContext.SchemaName;

    public string ReadPermission => MarketingPermissions.Read;

    public string WritePermission => MarketingPermissions.Write;

    public async Task<IReadOnlySet<Guid>> GetExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var array = ids as Guid[] ?? ids.ToArray();
        return (await db.Campaigns.AsNoTracking().Where(c => array.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
    }
}
