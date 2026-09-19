using Crm.Modules.Identity.Contracts;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;

namespace Crm.Modules.Identity.Application.AuditLog;

/// <summary>
/// Organizasyonun denetim kaydı (K14): en yeni önce, sayfalı. Kaynak ortak <c>audit.audit_log_entries</c> tablosudur;
/// kiracı filtresi altında okunur, yani yalnız aktif organizasyonun kayıtları döner.
/// </summary>
[RequiresPermission(OrgPermissions.AuditRead)]
public sealed record GetAuditLogQuery(PagedQuery Paging) : IQuery<AuditPageDto>;

public sealed class GetAuditLogHandler(IIdentityReadStore readStore) : IQueryHandler<GetAuditLogQuery, AuditPageDto>
{
    public async Task<Result<AuditPageDto>> Handle(GetAuditLogQuery query, CancellationToken cancellationToken) =>
        await readStore.GetAuditPageAsync(query.Paging.Page, query.Paging.PageSize, cancellationToken).ConfigureAwait(false);
}
