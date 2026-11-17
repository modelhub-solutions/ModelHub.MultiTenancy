using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ModelHub.MultiTenancy;

/// <summary>
/// Reads the tenant from the authenticated user and populates the scoped <see cref="TenantContext"/>.
/// Must run after <c>UseAuthentication()</c>.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;
    private readonly MultiTenancyOptions _options;

    /// <summary>Creates the middleware.</summary>
    public TenantResolutionMiddleware(
        RequestDelegate next,
        IOptions<MultiTenancyOptions> options,
        ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>Resolves the tenant for this request, then calls the next middleware.</summary>
    public Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        Resolve(context, tenantContext);
        return _next(context);
    }

    internal void Resolve(HttpContext context, TenantContext tenantContext)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            tenantContext.Set(null, isAuthenticated: false, isPlatformAdmin: false,
                isImpersonating: false, isSubTenantScoped: false, subTenantIds: null);
            return;
        }

        // 1. Platform admin, by role or by claim.
        var isPlatformAdmin = _options.PlatformAdminRoles.Any(user.IsInRole)
            || (_options.PlatformAdminClaimType is { } adminClaim
                && string.Equals(user.FindFirst(adminClaim)?.Value, "true", StringComparison.OrdinalIgnoreCase));

        // 2. Tenant from the token.
        var tenantId = ParseGuid(user.FindFirst(_options.TenantIdClaimType)?.Value);

        // 3. Impersonation header: platform admins only.
        var isImpersonating = false;
        if (_options.ImpersonationHeader is { } header
            && context.Request.Headers.TryGetValue(header, out var headerValues)
            && !string.IsNullOrWhiteSpace(headerValues.ToString()))
        {
            var requested = ParseGuid(headerValues.ToString());
            if (requested is null)
            {
                _logger.LogWarning("Ignored invalid {Header} value for user {UserId}.", header, UserId(user));
            }
            else if (isPlatformAdmin)
            {
                tenantId = requested;
                isPlatformAdmin = false; // scoped to the impersonated tenant: no bypass
                isImpersonating = true;
                _logger.LogInformation("Platform admin {UserId} is acting as tenant {TenantId} on {Path}.",
                    UserId(user), requested, context.Request.Path);
            }
            else
            {
                _logger.LogWarning(
                    "Ignored {Header} from non-admin user {UserId} (requested {Requested}, token tenant {TenantId}) on {Path}.",
                    header, UserId(user), requested, tenantId, context.Request.Path);
            }
        }

        // 4. Sub-tenant scoping.
        var subTenantIds = user.FindAll(_options.SubTenantIdClaimType)
            .Select(c => ParseGuid(c.Value))
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToArray();

        var isSubTenantScoped = !isPlatformAdmin
            && !_options.TenantAdminRoles.Any(user.IsInRole)
            && _options.SubTenantScopedRoles.Any(user.IsInRole);

        // 5. Audit the all-tenants bypass, as impersonation is audited above.
        if (isPlatformAdmin)
        {
            _logger.LogInformation("Platform admin {UserId} has all-tenant access on {Method} {Path}.",
                UserId(user), context.Request.Method, context.Request.Path);
        }

        tenantContext.Set(tenantId, isAuthenticated: true, isPlatformAdmin, isImpersonating,
            isSubTenantScoped, subTenantIds);
    }

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static string UserId(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? "(unknown)";
}
