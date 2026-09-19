using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Shared.Web.Authorization;

/// <summary>[HasPermission("leave.approve")] → policy "perm:leave.approve".</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public HasPermissionAttribute(string permission) => Policy = PolicyPrefix + permission;

    public string Permission => Policy![PolicyPrefix.Length..];
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>"perm:" ön ekli policy'leri dinamik üretir; diğerlerini varsayılan sağlayıcıya bırakır.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var permission = policyName[HasPermissionAttribute.PolicyPrefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}

/// <summary>İzin kararı: IPermissionService (HybridCache'li) aktif organizasyondaki rol izinlerine bakar.</summary>
public sealed class PermissionHandler(ICurrentUser user, IPermissionService permissions)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!user.IsAuthenticated || user.UserId is null)
        {
            return;
        }

        if (await permissions.HasAsync(user.UserId.Value, requirement.Permission).ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }
}
