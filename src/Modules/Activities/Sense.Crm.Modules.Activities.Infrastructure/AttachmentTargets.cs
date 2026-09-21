using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Activities.Contracts;
using Sense.Crm.Modules.Activities.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Files;

namespace Sense.Crm.Modules.Activities.Infrastructure;

/// <summary>
/// Activities'in dosya eki hedefi (M8C): aktivite. Kiracı + yumuşak silme filtresi altında tek sorgulukla var olanları döner; izin anahtarları
/// <see cref="ActivitiesPermissions"/> sabitlerindendir. Çekirdek modül (kapı yok).
/// </summary>
internal sealed class ActivityAttachmentTarget(ActivitiesDbContext db) : IAttachmentTarget
{
    public string RecordType => AttachmentRecordTypes.Activity;

    public string Module => ActivitiesDbContext.SchemaName;

    public string ReadPermission => ActivitiesPermissions.Read;

    public string WritePermission => ActivitiesPermissions.Write;

    public async Task<IReadOnlySet<Guid>> GetExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var array = ids as Guid[] ?? ids.ToArray();
        return (await db.Activities.AsNoTracking().Where(a => array.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
    }
}
