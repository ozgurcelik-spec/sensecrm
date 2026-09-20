using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Service.Contracts;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Files;

namespace Sense.Crm.Modules.Service.Infrastructure;

/// <summary>
/// Service'in dosya eki hedefi (M8C): talep (case). Kiracı + yumuşak silme filtresi altında tek sorgulukla var olanları döner; izin anahtarları
/// <see cref="ServicePermissions"/> sabitlerindendir. Kapı modülü <c>service</c>.
/// </summary>
internal sealed class CaseAttachmentTarget(ServiceDbContext db) : IAttachmentTarget
{
    public string RecordType => AttachmentRecordTypes.Case;

    public string Module => ServiceDbContext.SchemaName;

    public string ReadPermission => ServicePermissions.CasesRead;

    public string WritePermission => ServicePermissions.CasesWrite;

    public async Task<IReadOnlySet<Guid>> GetExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var array = ids as Guid[] ?? ids.ToArray();
        return (await db.Cases.AsNoTracking().Where(c => array.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
    }
}
