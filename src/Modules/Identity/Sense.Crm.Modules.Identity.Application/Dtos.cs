using System.Text.Json;

namespace Sense.Crm.Modules.Identity.Application;

// HTTP sözleşmesi (docs/architecture/backend.md): alan adları camelCase serileştirilir, null alanlar yazılmaz.

public sealed record AuthResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, bool MustChangePassword = false);

public sealed record MeDto(
    MeUserDto User,
    OrganizationDto Organization,
    RoleRefDto Role,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<OrganizationSummaryDto> Organizations);

public sealed record MeUserDto(Guid Id, string Email, string DisplayName, string Locale, bool IsPlatformAdmin, bool MustChangePassword = false);

public sealed record OrganizationDto(Guid Id, string Name, string Slug, string DefaultLocale, string TimeZone);

public sealed record OrganizationSummaryDto(Guid Id, string Name, string Slug);

public sealed record RoleRefDto(Guid Id, string Name);

public sealed record PermissionDto(string Key, string Group);

public sealed record MemberDto(Guid UserId, string Email, string DisplayName, Guid RoleId, string RoleName, bool IsActive, DateTimeOffset JoinedAt, string Status = MemberStatuses.Active, DateTimeOffset? InvitedAt = null);

/// <summary>Üye durumu tel değerleri (<c>status</c> alanı).</summary>
public static class MemberStatuses
{
    public const string Active = "active";
    public const string Pending = "pending";
}

/// <summary>Bekleyen organizasyon daveti (<c>GET /me/invitations</c>): <see cref="Id"/> davetin (üyelik satırının) kimliğidir.</summary>
public sealed record InvitationDto(Guid Id, Guid OrganizationId, string OrganizationName, string RoleName, DateTimeOffset InvitedAt);

/// <summary>
/// <c>POST /organization/members</c> yanıtı. Yeni hesapta <see cref="UserId"/> ve tek seferlik <see cref="TemporaryPassword"/> vardır
/// (<c>status: active</c>); mevcut hesapta yalnız <c>status: pending</c> döner ve başka organizasyona ait hiçbir hesap verisi sızmaz.
/// </summary>
public sealed record AddMemberResultDto(Guid? UserId, string Email, Guid RoleId, string RoleName, string Status, string? TemporaryPassword);

public sealed record RoleDto(Guid Id, string Name, bool IsSystem, IReadOnlyList<string> Permissions, int MemberCount);

/// <summary><see cref="Changes"/>: <c>{ "alan": { "old": ..., "new": ... } }</c>.</summary>
public sealed record AuditEntryDto(
    Guid Id,
    string EntityType,
    string EntityId,
    string Action,
    Guid? UserId,
    string? UserDisplayName,
    JsonElement Changes,
    DateTimeOffset OccurredAt);

public sealed record AuditPageDto(IReadOnlyList<AuditEntryDto> Items, long Total);
