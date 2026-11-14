namespace ModelHub.MultiTenancy.EntityFrameworkCore;

/// <summary>Query helpers for sub-tenant scoping.</summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Restricts the query to the user's sub-tenants when <see cref="ITenantContext.IsSubTenantScoped"/> is true;
    /// otherwise returns it unchanged. Rows with no sub-tenant (tenant-wide rows) are included only when
    /// <paramref name="includeTenantWide"/> is true.
    /// </summary>
    public static IQueryable<T> WhereVisibleToSubTenants<T>(
        this IQueryable<T> query, ITenantContext tenant, bool includeTenantWide = false)
        where T : ISubTenantEntity
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(tenant);

        if (!tenant.IsSubTenantScoped)
            return query;

        var ids = tenant.SubTenantIds.ToList();
        return includeTenantWide
            ? query.Where(e => e.SubTenantId == null || ids.Contains(e.SubTenantId.Value))
            : query.Where(e => e.SubTenantId != null && ids.Contains(e.SubTenantId.Value));
    }
}
