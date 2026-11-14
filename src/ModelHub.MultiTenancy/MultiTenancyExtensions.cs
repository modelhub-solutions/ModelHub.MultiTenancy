using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ModelHub.MultiTenancy;

/// <summary>Registration helpers.</summary>
public static class MultiTenancyExtensions
{
    /// <summary>
    /// Registers <see cref="ITenantContext"/> (scoped, one per request) and the options.
    /// </summary>
    public static IServiceCollection AddMultiTenancy(
        this IServiceCollection services,
        Action<MultiTenancyOptions>? configure = null)
    {
        services.AddOptions<MultiTenancyOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        return services;
    }

    /// <summary>
    /// Adds <see cref="TenantResolutionMiddleware"/>. Call after <c>UseAuthentication()</c>
    /// and before <c>UseAuthorization()</c>.
    /// </summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>();
}
