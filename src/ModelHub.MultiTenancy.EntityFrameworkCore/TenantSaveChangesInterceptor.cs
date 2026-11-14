using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ModelHub.MultiTenancy.EntityFrameworkCore;

/// <summary>
/// Before every save: stamps the current tenant on new <see cref="ITenantEntity"/> rows and refuses
/// any insert, update or delete that touches another tenant's data. Platform admins may write to any
/// tenant but must set <see cref="ITenantEntity.TenantId"/> explicitly.
/// </summary>
public sealed class TenantSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <summary>A shared instance; the interceptor is stateless.</summary>
    public static TenantSaveChangesInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Apply(DbContext? context)
    {
        if (context is not ITenantAwareDbContext { TenantContext: var tenant })
            return;

        foreach (var entry in context.ChangeTracker.Entries<ITenantEntity>())
        {
            var typeName = entry.Metadata.DisplayName();
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.TenantId == Guid.Empty)
                    {
                        if (!tenant.HasTenant)
                            throw new TenantMismatchException(
                                $"Cannot add {typeName}: there is no current tenant, so TenantId must be set explicitly.");
                        entry.Entity.TenantId = tenant.TenantId!.Value;
                    }
                    EnsureOwned(tenant, entry.Entity.TenantId, typeName, "add");
                    break;

                case EntityState.Modified:
                    var tenantProperty = entry.Property(e => e.TenantId);
                    if (tenantProperty.IsModified && tenantProperty.OriginalValue != tenantProperty.CurrentValue
                        && !tenant.IsPlatformAdmin)
                        throw new TenantMismatchException($"Cannot move {typeName} to another tenant.");
                    EnsureOwned(tenant, tenantProperty.OriginalValue, typeName, "update");
                    break;

                case EntityState.Deleted:
                    EnsureOwned(tenant, entry.Entity.TenantId, typeName, "delete");
                    break;
            }
        }
    }

    private static void EnsureOwned(ITenantContext tenant, Guid rowTenantId, string typeName, string action)
    {
        if (tenant.IsPlatformAdmin)
            return;
        if (!tenant.HasTenant || rowTenantId != tenant.TenantId)
            throw new TenantMismatchException($"Cannot {action} {typeName}: it belongs to a different tenant.");
    }
}

/// <summary>Registration helper for the save interceptor.</summary>
public static class DbContextOptionsBuilderExtensions
{
    /// <summary>Adds <see cref="TenantSaveChangesInterceptor"/> to the context.</summary>
    public static DbContextOptionsBuilder UseMultiTenancy(this DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(TenantSaveChangesInterceptor.Instance);

    /// <summary>Adds <see cref="TenantSaveChangesInterceptor"/> to the context.</summary>
    public static DbContextOptionsBuilder<TContext> UseMultiTenancy<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext =>
        optionsBuilder.AddInterceptors(TenantSaveChangesInterceptor.Instance);
}
