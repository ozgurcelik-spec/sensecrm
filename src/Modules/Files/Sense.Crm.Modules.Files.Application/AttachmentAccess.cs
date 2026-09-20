using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Files;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Files.Application;

/// <summary>
/// <see cref="IAttachmentAccess"/> uygulaması — erişim kararının <b>tek</b> yeri (D3). İzin <b>çalışma anında</b> <c>IPermissionService</c> ile denetlenir (kayıt türü
/// istekten gelir; statik <c>[RequiresPermission]</c> yetmez). İzin, hedef kaydın varlığından <b>önce</b> denetlenir: izinsiz çağıran kayıt kimliği yoklayamaz.
/// </summary>
public sealed class AttachmentAccess(
    ICurrentUser user,
    ITenantContext tenant,
    IPermissionService permissions,
    ITenantEntitlements entitlements,
    IEnumerable<IAttachmentTarget> targets,
    IFileAttachmentRepository files) : IAttachmentAccess
{
    private const string PermissionArg = "permission";

    private readonly Dictionary<string, IAttachmentTarget> _targets = targets
        .GroupBy(t => t.RecordType, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private enum PermissionCheck
    {
        Allowed,
        Denied,
        Unauthenticated,
    }

    public async Task<Result<AttachmentTargetInfo>> AuthorizeRecordAsync(string? recordType, Guid recordId, AttachmentAccessMode mode, CancellationToken ct)
    {
        if (!AttachmentRecordTypes.IsKnown(recordType) || recordId == Guid.Empty || !_targets.TryGetValue(recordType!, out var target))
        {
            return Error.Validation(ErrorCodes.ValidationError);
        }

        switch (await CheckPermissionAsync(target, mode, ct).ConfigureAwait(false))
        {
            case PermissionCheck.Unauthenticated:
                return Error.Unauthorized(ErrorCodes.Unauthenticated);
            case PermissionCheck.Denied:
                return Error.Forbidden(ErrorCodes.Forbidden, (PermissionArg, PermissionKey(target, mode)));
            default:
                break;
        }

        if (await CheckGateAsync(target, ct).ConfigureAwait(false) is { } gate)
        {
            return gate;
        }

        var existing = await target.GetExistingAsync([recordId], ct).ConfigureAwait(false);
        return existing.Contains(recordId) ? Info(target) : FilesErrors.RecordNotFoundError();
    }

    public async Task<Result<AuthorizedFile>> AuthorizeFileAsync(Guid fileId, AttachmentAccessMode mode, CancellationToken ct)
    {
        var file = await files.GetAsync(fileId, ct).ConfigureAwait(false);
        if (file is null || file.State == FileState.Deleted || !_targets.TryGetValue(file.RecordType, out var target))
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        switch (await CheckPermissionAsync(target, mode, ct).ConfigureAwait(false))
        {
            case PermissionCheck.Unauthenticated:
                return Error.Unauthorized(ErrorCodes.Unauthenticated);
            case PermissionCheck.Denied:
                // Dosya kimliğiyle OKUMA: varlık izinsiz çağırana sızmaz (404). Yazma: 403.
                return mode == AttachmentAccessMode.Read
                    ? Error.NotFound(ErrorCodes.NotFound)
                    : Error.Forbidden(ErrorCodes.Forbidden, (PermissionArg, PermissionKey(target, mode)));
            default:
                break;
        }

        if (await CheckGateAsync(target, ct).ConfigureAwait(false) is { } gate)
        {
            return gate;
        }

        var existing = await target.GetExistingAsync([file.RecordId], ct).ConfigureAwait(false);
        return existing.Contains(file.RecordId) ? new AuthorizedFile(file, Info(target)) : Error.NotFound(ErrorCodes.NotFound);
    }

    private static string PermissionKey(IAttachmentTarget target, AttachmentAccessMode mode) =>
        mode == AttachmentAccessMode.Read ? target.ReadPermission : target.WritePermission;

    private async Task<PermissionCheck> CheckPermissionAsync(IAttachmentTarget target, AttachmentAccessMode mode, CancellationToken ct)
    {
        if (!user.IsAuthenticated || user.UserId is not { } userId)
        {
            return PermissionCheck.Unauthenticated;
        }

        return await permissions.HasAsync(userId, PermissionKey(target, mode), ct).ConfigureAwait(false) ? PermissionCheck.Allowed : PermissionCheck.Denied;
    }

    /// <summary>Kapı modülü planda kapalıysa 403 <c>plan.module_disabled</c> (okuma dahil).</summary>
    private async Task<Error?> CheckGateAsync(IAttachmentTarget target, CancellationToken ct)
    {
        var snapshot = await entitlements.GetAsync(tenant.TenantId, ct).ConfigureAwait(false);
        return snapshot.IsModuleEnabled(target.Module) ? null : EntitlementErrors.DisabledModule(target.Module);
    }

    private static AttachmentTargetInfo Info(IAttachmentTarget target) =>
        new(target.RecordType, target.Module, target.ReadPermission, target.WritePermission);
}
