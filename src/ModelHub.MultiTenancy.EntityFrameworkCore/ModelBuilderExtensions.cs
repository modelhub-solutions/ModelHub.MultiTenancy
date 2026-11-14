using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace ModelHub.MultiTenancy.EntityFrameworkCore;

/// <summary>Adds tenant query filters to the model.</summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// The filter name used on EF Core 10+, where several named filters can sit on one entity.
    /// Disable just this filter with <c>IgnoreQueryFilters([TenantFilterName])</c>.
    /// </summary>
    public const string TenantFilterName = "ModelHub.MultiTenancy.Tenant";

    private static readonly PropertyInfo TenantContextProperty =
        typeof(ITenantAwareDbContext).GetProperty(nameof(ITenantAwareDbContext.TenantContext))!;
    private static readonly PropertyInfo IsPlatformAdminProperty =
        typeof(ITenantContext).GetProperty(nameof(ITenantContext.IsPlatformAdmin))!;
    private static readonly PropertyInfo TenantIdProperty =
        typeof(ITenantContext).GetProperty(nameof(ITenantContext.TenantId))!;
    private static readonly PropertyInfo EntityTenantIdProperty =
        typeof(ITenantEntity).GetProperty(nameof(ITenantEntity.TenantId))!;

    /// <summary>
    /// Adds <c>IsPlatformAdmin || TenantId == current tenant</c> to every root entity that implements
    /// <see cref="ITenantEntity"/>. A request with no tenant sees no tenant rows.
    /// Call from <c>OnModelCreating</c>, passing <c>this</c>.
    /// </summary>
    /// <remarks>
    /// On EF Core 8 an entity has one filter, so this combines with a filter already declared on the entity
    /// (e.g. soft delete). Call it <b>after</b> your own <c>HasQueryFilter</c> calls, or they will replace it.
    /// On EF Core 10+ it is added as the named filter <see cref="TenantFilterName"/> and order does not matter.
    /// </remarks>
    public static ModelBuilder ApplyTenantQueryFilters<TContext>(this ModelBuilder modelBuilder, TContext context)
        where TContext : DbContext, ITenantAwareDbContext
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        // EF Core recognises a constant of the context type and swaps in the current
        // context instance at query time, so one cached model serves every tenant.
        var contextExpression = Expression.Constant(context, typeof(TContext));
        var tenantContext = Expression.Property(contextExpression, TenantContextProperty);
        var isPlatformAdmin = Expression.Property(tenantContext, IsPlatformAdminProperty);
        var currentTenantId = Expression.Property(tenantContext, TenantIdProperty);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (entityType.BaseType is not null || entityType.IsOwned()
                || !typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType))
                continue;

            var entity = Expression.Parameter(entityType.ClrType, "e");
            var entityTenantId = Expression.Convert(
                Expression.Property(Expression.Convert(entity, typeof(ITenantEntity)), EntityTenantIdProperty),
                typeof(Guid?));
            var body = Expression.OrElse(isPlatformAdmin, Expression.Equal(entityTenantId, currentTenantId));
            var filter = Expression.Lambda(body, entity);

#if NET10_0_OR_GREATER
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(TenantFilterName, filter);
#else
            var existing = entityType.GetQueryFilter();
            if (existing is not null)
            {
                var existingBody = new ParameterReplacer(existing.Parameters[0], entity).Visit(existing.Body);
                filter = Expression.Lambda(Expression.AndAlso(existingBody, body), entity);
            }
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(filter);
#endif
        }

        return modelBuilder;
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
