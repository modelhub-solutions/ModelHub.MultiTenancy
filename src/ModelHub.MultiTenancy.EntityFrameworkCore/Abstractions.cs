namespace ModelHub.MultiTenancy.EntityFrameworkCore;

/// <summary>An entity that belongs to exactly one tenant.</summary>
public interface ITenantEntity
{
    /// <summary>The owning tenant. Stamped automatically on insert by <see cref="TenantSaveChangesInterceptor"/>.</summary>
    Guid TenantId { get; set; }
}

/// <summary>An entity that belongs to a sub-tenant (for example, a branch) within its tenant.</summary>
public interface ISubTenantEntity
{
    /// <summary>The owning sub-tenant, or <c>null</c> for rows that apply to the whole tenant.</summary>
    Guid? SubTenantId { get; }
}

/// <summary>
/// Implemented by a <see cref="Microsoft.EntityFrameworkCore.DbContext"/> so the tenant filters and the
/// save interceptor can read the current request's tenant.
/// </summary>
public interface ITenantAwareDbContext
{
    /// <summary>The tenant for this context instance (inject <see cref="ITenantContext"/> in the constructor).</summary>
    ITenantContext TenantContext { get; }
}

/// <summary>Thrown when a save would write data outside the current tenant.</summary>
public sealed class TenantMismatchException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public TenantMismatchException(string message) : base(message) { }
}
