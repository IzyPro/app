using BTCPayApp.Core.Helpers;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BTCPayApp.Core.Auth;

public class AuthorizationHandler(IOptionsMonitor<IdentityOptions> identityOptions) : AuthorizationHandler<PolicyRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PolicyRequirement requirement)
    {
        if (context.User.Identity?.AuthenticationType != "Greenfield")
            return Task.CompletedTask;

        var userId = context.User.Claims.FirstOrDefault(c => c.Type == identityOptions.CurrentValue.ClaimsIdentity.UserIdClaimType)?.Value;
        if (string.IsNullOrEmpty(userId))
            return Task.CompletedTask;

        var permissionSet = new PermissionSet();
        var success = false;
        var isAdmin = context.User.IsInRole("ServerAdmin");
        var isOwner = context.User.IsInRole("DeviceOwner");
        var storeId = context.Resource as string;
        var policy = requirement.Policy;
        var requiredUnscoped = false;
        if (policy.EndsWith(':'))
        {
            policy = policy[..^1];
            requiredUnscoped = true;
            storeId = null;
        }

        if (!string.IsNullOrEmpty(storeId))
        {
            var permissions = context.User.Claims.FirstOrDefault(c => c.Type == storeId)?.Value;
            if (!string.IsNullOrEmpty(permissions))
            {
                permissionSet = new PermissionSet(permissions.Split(',')
                    .Select(s => Permission.TryParse(s, out var permission) ? permission : null)
                    .Where(s => s != null).ToArray());
            }
        }

        if (Policies.IsServerPolicy(policy) && isAdmin)
        {
            success = true;
        }
        else if (Policies.IsUserPolicy(policy) && !string.IsNullOrEmpty(userId))
        {
            success = true;
        }
        else if (Policies.IsStorePolicy(policy) && !string.IsNullOrEmpty(storeId))
        {
            if (!success && permissionSet.Contains(policy, storeId))
            {
                success = true;
            }

            if (!success && requiredUnscoped && string.IsNullOrEmpty(storeId))
            {
                success = true;
            }
        }
        else if (Policies.IsPluginPolicy(policy) && policy.StartsWith("btcpay.plugin.app"))
        {
            success = isOwner;
        }
        if (success)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
