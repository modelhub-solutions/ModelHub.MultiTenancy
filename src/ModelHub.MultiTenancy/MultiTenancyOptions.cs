namespace ModelHub.MultiTenancy;

/// <summary>Configures how the tenant is read from the signed-in user.</summary>
public sealed class MultiTenancyOptions
{
    /// <summary>Claim holding the tenant id (a GUID). Default: <c>tenant_id</c>.</summary>
    public string TenantIdClaimType { get; set; } = "tenant_id";

    /// <summary>Claim holding a sub-tenant id; may appear several times. Default: <c>sub_tenant_id</c>.</summary>
    public string SubTenantIdClaimType { get; set; } = "sub_tenant_id";

    /// <summary>
    /// Claim that marks a platform admin when its value is <c>true</c>. Default: <c>is_platform_admin</c>.
    /// Set to <c>null</c> to rely on <see cref="PlatformAdminRoles"/> only.
    /// </summary>
    public string? PlatformAdminClaimType { get; set; } = "is_platform_admin";

    /// <summary>Roles that make a user a platform admin (bypass tenant filters).</summary>
    public ISet<string> PlatformAdminRoles { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Roles that see the whole tenant even if they also hold a sub-tenant-scoped role.</summary>
    public ISet<string> TenantAdminRoles { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Roles whose data is restricted to their assigned sub-tenants.</summary>
    public ISet<string> SubTenantScopedRoles { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Request header a platform admin can send to act as a single tenant. Default: <c>X-Tenant-Id</c>.
    /// Ignored (and logged) for everyone else. Set to <c>null</c> to disable impersonation.
    /// </summary>
    public string? ImpersonationHeader { get; set; } = "X-Tenant-Id";
}
