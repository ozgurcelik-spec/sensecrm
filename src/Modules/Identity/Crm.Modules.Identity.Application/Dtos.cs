using System.Text.Json;

namespace Crm.Modules.Identity.Application;

// HTTP sözleşmesi (docs/architecture/backend.md): alan adları camelCase serileştirilir, null alanlar yazılmaz.

public sealed record AuthResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public sealed record MeDto(
    MeUserDto User,
    OrganizationDto Organization,
    RoleRefDto Role,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<OrganizationSummaryDto> Organizations);

public sealed record MeUserDto(Guid Id, string Email, string DisplayName, string Locale, bool IsPlatformAdmin);

public sealed record OrganizationDto(Guid Id, string Name, string Slug, string DefaultLocale, string TimeZone);

public sealed record OrganizationSummaryDto(Guid Id, string Name, string Slug);

public sealed record RoleRefDto(Guid Id, string Name);

public sealed record PermissionDto(string Key, string Group);

public sealed record MemberDto(Guid UserId, string Email, string DisplayName, Guid RoleId, string RoleName, bool IsActive, DateTimeOffset JoinedAt);

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
