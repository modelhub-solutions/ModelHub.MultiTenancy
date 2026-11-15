using System.Diagnostics;

namespace ModelHub.MultiTenancy;

/// <summary>
/// Default <see cref="ITenantContext"/> implementation. Populated once per request by
/// <see cref="TenantResolutionMiddleware"/>, or by <see cref="SetTenant"/> in background work.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public sealed class TenantContext : ITenantContext
{
    private IReadOnlyList<Guid> _subTenantIds = Array.Empty<Guid>();
    private bool _isSet;

    /// <inheritdoc />
    public Guid? TenantId { get; private set; }

    /// <inheritdoc />
    public bool HasTenant => TenantId.HasValue;

    /// <inheritdoc />
    public bool IsAuthenticated { get; private set; }

    /// <inheritdoc />
    public bool IsPlatformAdmin { get; private set; }

    /// <inheritdoc />
    public bool IsImpersonating { get; private set; }

    /// <inheritdoc />
    public bool IsSubTenantScoped { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<Guid> SubTenantIds => _subTenantIds;

    /// <summary>Creates a context for one tenant, e.g. for a background job or a test.</summary>
    public static TenantContext ForTenant(Guid tenantId, IEnumerable<Guid>? subTenantIds = null)
    {
        var context = new TenantContext();
        context.SetTenant(tenantId, subTenantIds);
        return context;
    }

    /// <summary>Creates a platform-admin context that sees every tenant, e.g. for maintenance jobs.</summary>
    public static TenantContext ForPlatformAdmin()
    {
        var context = new TenantContext();
        context.Set(tenantId: null, isAuthenticated: true, isPlatformAdmin: true,
            isImpersonating: false, isSubTenantScoped: false, subTenantIds: null);
        return context;
    }

    /// <summary>
    /// Sets the tenant for work that does not run through the HTTP pipeline (queues,
    /// scheduled jobs). Pass <paramref name="subTenantIds"/> to restrict the work to those sub-tenants.
    /// </summary>
    /// <exception cref="InvalidOperationException">The context has already been set for this scope.</exception>
    public void SetTenant(Guid tenantId, IEnumerable<Guid>? subTenantIds = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));

        var ids = subTenantIds?.ToArray();
        Set(tenantId, isAuthenticated: true, isPlatformAdmin: false, isImpersonating: false,
            isSubTenantScoped: ids is { Length: > 0 }, subTenantIds: ids);
    }

    internal void Set(Guid? tenantId, bool isAuthenticated, bool isPlatformAdmin, bool isImpersonating,
        bool isSubTenantScoped, IEnumerable<Guid>? subTenantIds)
    {
        if (_isSet)
            throw new InvalidOperationException(
                "The tenant context has already been set for this scope. Create a new scope to act for another tenant.");

        _isSet = true;
        TenantId = tenantId;
        IsAuthenticated = isAuthenticated;
        IsPlatformAdmin = isPlatformAdmin;
        IsImpersonating = isImpersonating;
        IsSubTenantScoped = isSubTenantScoped;
        _subTenantIds = (subTenantIds ?? Enumerable.Empty<Guid>()).Distinct().ToArray();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (!IsAuthenticated)
            return "Anonymous";

        if (IsImpersonating && TenantId.HasValue)
            return $"Platform admin acting as tenant {TenantId}";

        if (IsPlatformAdmin)
            return "Platform admin (all tenants)";

        if (!TenantId.HasValue)
            return "No tenant";

        var result = $"Tenant {TenantId}";
        if (IsSubTenantScoped && SubTenantIds.Count > 0)
        {
            result += $" (sub-tenants: {SubTenantIds.Count})";
        }
        return result;
    }
}
