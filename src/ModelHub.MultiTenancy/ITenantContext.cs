namespace ModelHub.MultiTenancy;

/// <summary>
/// The tenant the current request (or background job) is acting for.
/// Registered as a scoped service: one instance per request.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// The active tenant. <c>null</c> when the request is anonymous, when the user has no
    /// tenant claim, or when a platform admin is acting across all tenants.
    /// </summary>
    Guid? TenantId { get; }

    /// <summary><c>true</c> when <see cref="TenantId"/> has a value.</summary>
    bool HasTenant { get; }

    /// <summary><c>true</c> when the request carries an authenticated user.</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// <c>true</c> when the user is a platform admin acting across all tenants.
    /// Tenant query filters are bypassed. Always <c>false</c> while impersonating.
    /// </summary>
    bool IsPlatformAdmin { get; }

    /// <summary>
    /// <c>true</c> when a platform admin is acting as a single tenant through the
    /// impersonation header. The admin bypass is switched off for the request.
    /// </summary>
    bool IsImpersonating { get; }

    /// <summary>
    /// <c>true</c> when the user's data must be restricted to the sub-tenants in
    /// <see cref="SubTenantIds"/> (for example, a branch manager who sees only their branches).
    /// </summary>
    bool IsSubTenantScoped { get; }

    /// <summary>The sub-tenants the user is assigned to (from the token).</summary>
    IReadOnlyList<Guid> SubTenantIds { get; }
}
