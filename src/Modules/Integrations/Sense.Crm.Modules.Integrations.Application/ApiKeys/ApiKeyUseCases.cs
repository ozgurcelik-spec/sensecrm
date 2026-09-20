using FluentValidation;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application.Security;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Domain.ApiKeys;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Integrations.Application.ApiKeys;

/// <summary>Kapsam yükseltme reddi (<c>role.permission_escalation</c>): Identity'nin mevcut kodu, anahtar için de aynen (Administrator istisnası <b>yok</b>).</summary>
internal static class ApiKeyErrors
{
    public const string PermissionEscalation = "role.permission_escalation";
}

public static class ApiKeyMapper
{
    public static ApiKeyDto ToDto(ApiKey k, DateTime now, string? createdByName, string? revokedByName, string? rawKey = null) =>
        new(
            k.Id,
            k.Name,
            k.Description,
            ApiKeyToken.Display(k.Prefix),
            k.Scopes,
            k.ExpiresAt,
            k.AllowedCidrs,
            k.StatusAt(now),
            k.CreatedAt == default ? now : k.CreatedAt,
            k.CreatedByUserId,
            createdByName,
            k.LastUsedAt,
            k.LastUsedIp,
            k.RevokedAt,
            revokedByName,
            rawKey);

    /// <summary>İstemci ağı listesi: en çok <paramref name="max"/> geçerli CIDR; <c>/0</c> (tüm internet) reddedilir.</summary>
    public static bool ValidCidrs(IReadOnlyList<string>? cidrs, int max)
    {
        if (cidrs is null)
        {
            return true;
        }

        return cidrs.Count <= max && cidrs.All(c => Cidr.TryParse(c, out var parsed) && !parsed.IsAny);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Sorgular
// ---------------------------------------------------------------------------------------------------------------------

/// <summary><c>GET /integrations/api-keys</c>: <c>q</c>, <c>status</c> (<c>active|expired|revoked</c>), sıralama <c>createdAt</c> (varsayılan <c>-createdAt</c>), <c>name</c>, <c>lastUsedAt</c>, <c>expiresAt</c>.</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record ListApiKeysQuery(PagedQuery Paging, string? Status) : IQuery<PagedResult<ApiKeyDto>>;

public sealed class ListApiKeysValidator : AbstractValidator<ListApiKeysQuery>
{
    public ListApiKeysValidator() =>
        RuleFor(x => x.Status).Must(s => s is ApiKeyStatuses.Active or ApiKeyStatuses.Expired or ApiKeyStatuses.Revoked).When(x => !string.IsNullOrWhiteSpace(x.Status));
}

public sealed class ListApiKeysHandler(IApiKeyReadStore store, TimeProvider clock) : IQueryHandler<ListApiKeysQuery, PagedResult<ApiKeyDto>>
{
    public async Task<Result<PagedResult<ApiKeyDto>>> Handle(ListApiKeysQuery query, CancellationToken cancellationToken) =>
        await store.ListAsync(query.Paging, string.IsNullOrWhiteSpace(query.Status) ? null : query.Status, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record GetApiKeyQuery(Guid Id) : IQuery<ApiKeyDto>;

public sealed class GetApiKeyHandler(IApiKeyReadStore store, TimeProvider clock) : IQueryHandler<GetApiKeyQuery, ApiKeyDto>
{
    public async Task<Result<ApiKeyDto>> Handle(GetApiKeyQuery query, CancellationToken cancellationToken)
    {
        var dto = await store.GetAsync(query.Id, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return dto is null ? Error.NotFound(ErrorCodes.NotFound) : dto;
    }
}

/// <summary><c>GET …/usage?from&amp;to</c>: UTC günleri (varsayılan son 30 gün, ≤ 180) → günlük istek/hata/kısıtlama sayaçları (gün artan).</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record GetApiKeyUsageQuery(Guid Id, DateOnly? From, DateOnly? To) : IQuery<ApiKeyUsageDto>;

public sealed class GetApiKeyUsageValidator : AbstractValidator<GetApiKeyUsageQuery>
{
    public const int MaxRangeDays = 180;

    public GetApiKeyUsageValidator()
    {
        RuleFor(x => x.To).Must((q, to) => to!.Value >= q.From!.Value).When(x => x.From is not null && x.To is not null);
        RuleFor(x => x.To).Must((q, to) => to!.Value.DayNumber - q.From!.Value.DayNumber <= MaxRangeDays).When(x => x.From is not null && x.To is not null);
    }
}

public sealed class GetApiKeyUsageHandler(IApiKeyReadStore store, TimeProvider clock) : IQueryHandler<GetApiKeyUsageQuery, ApiKeyUsageDto>
{
    private const int DefaultDays = 30;

    public async Task<Result<ApiKeyUsageDto>> Handle(GetApiKeyUsageQuery query, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (await store.GetAsync(query.Id, now, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var to = query.To ?? DateOnly.FromDateTime(now);
        var from = query.From ?? to.AddDays(-(DefaultDays - 1));
        if (to.DayNumber - from.DayNumber > GetApiKeyUsageValidator.MaxRangeDays)
        {
            from = to.AddDays(-GetApiKeyUsageValidator.MaxRangeDays);
        }

        return new ApiKeyUsageDto(await store.GetUsageAsync(query.Id, from, to, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// <c>GET /integrations/api-keys/current</c>: <b>yalnız anahtar kimliğiyle</b> (<c>[AnyAuthenticatedUser]</c> + <c>[ApiKeyAllowed]</c>; JWT ile <c>403 forbidden</c>). İstemcinin anahtarını sınaması içindir:
/// <c>effectiveScopes</c> = kapsam ∩ oluşturanın o anki izinleri.
/// </summary>
[AnyAuthenticatedUser("Makine istemcisi kendi API anahtarını sınar; yalnız kendi kimliğini (kapsam/bitiş) döner, başka veri okumaz.")]
[ApiKeyAllowed("İstemci anahtarının geçerliliğini/kapsamını sınamak için tek izinsiz uç.")]
public sealed record GetCurrentApiKeyQuery : IQuery<CurrentApiKeyDto>;

public sealed class GetCurrentApiKeyHandler(ICurrentUser user, ITenantContext tenant, IPermissionService permissions, IApiKeyReadStore store)
    : IQueryHandler<GetCurrentApiKeyQuery, CurrentApiKeyDto>
{
    public async Task<Result<CurrentApiKeyDto>> Handle(GetCurrentApiKeyQuery query, CancellationToken cancellationToken)
    {
        if (user.ApiKey is not { } key || user.UserId is not { } userId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        var expiresAt = await store.GetExpiryAsync(key.KeyId, cancellationToken).ConfigureAwait(false);
        if (expiresAt is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        // Dekoratör (ApiKeyScopedPermissionService) anahtar isteklerinde zaten kapsam ∩ oluşturanın izinlerini döner.
        var effective = await permissions.GetPermissionsAsync(userId, cancellationToken).ConfigureAwait(false);
        return new CurrentApiKeyDto(
            key.KeyId,
            key.Name,
            ApiKeyToken.Display(key.Prefix),
            [.. key.Scopes.Order(StringComparer.Ordinal)],
            [.. effective.Order(StringComparer.Ordinal)],
            expiresAt.Value,
            tenant.TenantId);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Komutlar
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// <c>POST /integrations/api-keys</c> → 201 anahtar + <c>key</c> (ham anahtar yalnız burada). Sıra: doğrulama (bilinen kapsam, süre, CIDR) → izinli küme (<c>400 api_key.scope_not_allowed</c>:
/// <c>crm.*</c> eksi <c>crm.approvals.decide</c>) → oluşturanın etkin izinlerinin alt kümesi (<c>403 role.permission_escalation</c>; <b>Administrator istisnası yok</b>) → limit (<c>402</c>).
/// <c>expiresAt</c> verilmezse <c>DefaultLifetimeDays</c> (365), en çok <c>MaxLifetimeDays</c> (730); sınırsız anahtar yoktur.
/// </summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
[ConsumesLimit(LimitKeys.ApiKeys)]
public sealed record CreateApiKeyCommand(string Name, IReadOnlyList<string> Scopes, DateTime? ExpiresAt, IReadOnlyList<string>? AllowedCidrs, string? Description) : ICommand<ApiKeyDto>;

public sealed class CreateApiKeyValidator : AbstractValidator<CreateApiKeyCommand>
{
    public CreateApiKeyValidator(IApiKeyScopeCatalog catalog, IOptions<IntegrationsOptions> options, TimeProvider clock)
    {
        var settings = options.Value.ApiKeys;
        RuleFor(x => x.Name).NotEmpty().MaximumLength(IntegrationsLimits.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(IntegrationsLimits.DescriptionMaxLength);
        RuleFor(x => x.Scopes)
            .NotNull()
            .Must(s => s is { Count: >= 1 and <= IntegrationsLimits.ApiKeyScopesMax })
            .WithMessage("{PropertyName} must contain 1-40 permission keys.")
            .Must(s => s.All(catalog.AllKnown.Contains))
            .WithMessage("{PropertyName} contains an unknown permission key.");
        RuleFor(x => x.ExpiresAt)
            .Must(at => at!.Value.ToUniversalTime() > clock.GetUtcNow().UtcDateTime)
            .WithMessage("{PropertyName} must be in the future.")
            .Must(at => at!.Value.ToUniversalTime() <= clock.GetUtcNow().UtcDateTime.AddDays(settings.MaxLifetimeDays))
            .WithMessage("{PropertyName} exceeds the maximum key lifetime.")
            .When(x => x.ExpiresAt is not null);
        RuleFor(x => x.AllowedCidrs)
            .Must(c => ApiKeyMapper.ValidCidrs(c, settings.MaxCidrsPerKey))
            .WithMessage("{PropertyName} must contain at most " + settings.MaxCidrsPerKey + " valid CIDR ranges (0.0.0.0/0 and ::/0 are rejected).");
    }
}

public sealed class CreateApiKeyHandler(
    IApiKeyRepository keys,
    IApiKeyScopeCatalog catalog,
    IPermissionService permissions,
    IOptions<IntegrationsOptions> options,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<CreateApiKeyCommand, ApiKeyDto>
{
    public async Task<Result<ApiKeyDto>> Handle(CreateApiKeyCommand command, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } creator)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        // 1) İzinli küme: crm.* eksi crm.approvals.decide (org.* hiçbir anahtarda olmaz).
        var allowed = catalog.Allowed;
        var notAllowed = command.Scopes.FirstOrDefault(s => !allowed.Contains(s, StringComparer.Ordinal));
        if (notAllowed is not null)
        {
            return Error.Validation(ApiKeyErrorCodes.ScopeNotAllowed, (IntegrationsErrors.ScopeArg, notAllowed));
        }

        // 2) Oluşturanın ETKİN izinlerinin alt kümesi (Administrator istisnası yok).
        var held = await permissions.GetPermissionsAsync(creator, cancellationToken).ConfigureAwait(false);
        var missing = command.Scopes.FirstOrDefault(s => !held.Contains(s));
        if (missing is not null)
        {
            return Error.Forbidden(ApiKeyErrors.PermissionEscalation, (IntegrationsErrors.ScopeArg, missing));
        }

        if (await keys.NameExistsAsync(command.Name.Trim(), excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(ApiKeyErrorCodes.NameTaken);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var expiresAt = command.ExpiresAt?.ToUniversalTime() ?? now.AddDays(options.Value.ApiKeys.DefaultLifetimeDays);
        ApiKeyParts parts;
        var attempts = 0;
        do
        {
            parts = ApiKeyToken.Generate(tenant.TenantId);
            if (++attempts > 8)
            {
                return Error.Failure(ErrorCodes.InternalError);
            }
        }
        while (await keys.PrefixExistsAsync(parts.Prefix, cancellationToken).ConfigureAwait(false));

        var key = ApiKey.Create(
            Guid.CreateVersion7(),
            tenant.TenantId,
            command.Name,
            command.Description,
            parts.Prefix,
            ApiKeyToken.HashSecret(parts.Secret),
            command.Scopes,
            expiresAt,
            command.AllowedCidrs ?? [],
            creator);
        keys.Add(key);
        return ApiKeyMapper.ToDto(key, now, user.DisplayName, null, ApiKeyToken.Format(tenant.TenantId, parts.Prefix, parts.Secret));
    }
}

/// <summary><c>PATCH /integrations/api-keys/{id}</c> → 204: yalnız ad/açıklama/IP listesi (kapsam ve bitiş değişmez; iptalli anahtar <c>409 api_key.revoked</c>). Gönderilmeyen alan korunur.</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record UpdateApiKeyCommand(Guid Id, string? Name, string? Description, bool DescriptionSet, IReadOnlyList<string>? AllowedCidrs) : ICommand;

public sealed class UpdateApiKeyValidator : AbstractValidator<UpdateApiKeyCommand>
{
    public UpdateApiKeyValidator(IOptions<IntegrationsOptions> options)
    {
        var settings = options.Value.ApiKeys;
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(IntegrationsLimits.NameMaxLength).When(x => x.Name is not null);
        RuleFor(x => x.Description).MaximumLength(IntegrationsLimits.DescriptionMaxLength);
        RuleFor(x => x.AllowedCidrs)
            .Must(c => ApiKeyMapper.ValidCidrs(c, settings.MaxCidrsPerKey))
            .WithMessage("{PropertyName} must contain at most " + settings.MaxCidrsPerKey + " valid CIDR ranges (0.0.0.0/0 and ::/0 are rejected).");
    }
}

public sealed class UpdateApiKeyHandler(IApiKeyRepository keys, IApiKeyCacheInvalidator cache, ITenantContext tenant) : ICommandHandler<UpdateApiKeyCommand>
{
    public async Task<Result> Handle(UpdateApiKeyCommand command, CancellationToken cancellationToken)
    {
        var key = await keys.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var name = command.Name ?? key.Name;
        if (!string.Equals(name.Trim(), key.Name, StringComparison.OrdinalIgnoreCase)
            && await keys.NameExistsAsync(name.Trim(), key.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(ApiKeyErrorCodes.NameTaken);
        }

        var updated = key.UpdateMeta(name, command.DescriptionSet ? command.Description : key.Description, command.AllowedCidrs ?? key.AllowedCidrs);
        if (updated.IsFailure)
        {
            return updated;
        }

        await cache.InvalidateAsync(tenant.TenantId, key.Prefix, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary><c>POST …/revoke</c> → 204 (idempotent): bu süreçte anında, ≤ <c>CacheSeconds</c> diğer kopyalarda geçersiz olur.</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record RevokeApiKeyCommand(Guid Id) : ICommand;

public sealed class RevokeApiKeyHandler(IApiKeyRepository keys, IApiKeyCacheInvalidator cache, ITenantContext tenant, ICurrentUser user, TimeProvider clock) : ICommandHandler<RevokeApiKeyCommand>
{
    public async Task<Result> Handle(RevokeApiKeyCommand command, CancellationToken cancellationToken)
    {
        var key = await keys.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        key.Revoke(user.UserId, clock.GetUtcNow().UtcDateTime);
        await cache.InvalidateAsync(tenant.TenantId, key.Prefix, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary><c>DELETE /integrations/api-keys/{id}</c> → 204 yalnız <b>iptal edilmiş veya süresi dolmuş</b> anahtar; aktif → <c>409 api_key.active</c>.</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record DeleteApiKeyCommand(Guid Id) : ICommand;

public sealed class DeleteApiKeyHandler(IApiKeyRepository keys, IApiKeyCacheInvalidator cache, ITenantContext tenant, TimeProvider clock) : ICommandHandler<DeleteApiKeyCommand>
{
    public async Task<Result> Handle(DeleteApiKeyCommand command, CancellationToken cancellationToken)
    {
        var key = await keys.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (key.IsActive(clock.GetUtcNow().UtcDateTime))
        {
            return Error.Conflict(ApiKeyErrorCodes.Active);
        }

        keys.Remove(key);
        await cache.InvalidateAsync(tenant.TenantId, key.Prefix, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
