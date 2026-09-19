using Crm.Modules.Identity.Contracts;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

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

/// <summary>
/// Kayıt bazlı denetim geçmişi (<c>GET /audit?entityType&amp;entityId</c>). Yetki: <c>org.audit.read</c> veya varlık türünün
/// kaynağının okuma izni (<see cref="IAuditEntityPermissions"/>, ör. Account → <c>crm.accounts.read</c>). Modüllerin bildirmediği
/// türler yalnız <c>org.audit.read</c> ile okunur.
/// </summary>
public sealed record GetEntityAuditQuery(string EntityType, string EntityId, PagedQuery Paging) : IQuery<AuditPageDto>;

public sealed class GetEntityAuditValidator : AbstractValidator<GetEntityAuditQuery>
{
    private const int EntityTypeMaxLength = 100;
    private const int EntityIdMaxLength = 64;

    public GetEntityAuditValidator()
    {
        RuleFor(x => x.EntityType).NotEmpty().MaximumLength(EntityTypeMaxLength);
        RuleFor(x => x.EntityId).NotEmpty().MaximumLength(EntityIdMaxLength);
    }
}

public sealed class GetEntityAuditHandler(
    IIdentityReadStore readStore,
    ICurrentUser user,
    IPermissionService permissions,
    IEnumerable<IAuditEntityPermissions> entityPermissions) : IQueryHandler<GetEntityAuditQuery, AuditPageDto>
{
    public async Task<Result<AuditPageDto>> Handle(GetEntityAuditQuery query, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var allowed = await permissions.HasAsync(userId, OrgPermissions.AuditRead, cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            var required = entityPermissions
                .Select(p => p.ReadPermissionsByEntityType.TryGetValue(query.EntityType, out var permission) ? permission : null)
                .FirstOrDefault(p => p is not null);
            allowed = required is not null && await permissions.HasAsync(userId, required, cancellationToken).ConfigureAwait(false);
        }

        if (!allowed)
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        return await readStore.GetEntityAuditPageAsync(query.EntityType, query.EntityId, query.Paging.Page, query.Paging.PageSize, cancellationToken).ConfigureAwait(false);
    }
}
