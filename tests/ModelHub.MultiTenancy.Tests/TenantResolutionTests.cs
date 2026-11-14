using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ModelHub.MultiTenancy.Tests;

public class TenantResolutionTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Branch1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Branch2 = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private static MultiTenancyOptions Options(Action<MultiTenancyOptions>? configure = null)
    {
        var options = new MultiTenancyOptions();
        options.PlatformAdminRoles.Add("PLATFORM_ADMIN");
        options.TenantAdminRoles.Add("TENANT_ADMIN");
        options.SubTenantScopedRoles.Add("BRANCH_MANAGER");
        configure?.Invoke(options);
        return options;
    }

    private static TenantContext Resolve(HttpContext http, MultiTenancyOptions? options = null)
    {
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask,
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            NullLogger<TenantResolutionMiddleware>.Instance);
        var tenant = new TenantContext();
        middleware.Resolve(http, tenant);
        return tenant;
    }

    private static HttpContext Http(IEnumerable<Claim>? claims, string? header = null)
    {
        var http = new DefaultHttpContext();
        if (claims is not null)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "sub", "role"));
        if (header is not null)
            http.Request.Headers["X-Tenant-Id"] = header;
        return http;
    }

    private static Claim C(string type, object value) => new(type, value.ToString()!);

    [Fact]
    public void Anonymous_request_has_no_tenant()
    {
        var tenant = Resolve(Http(null));

        Assert.False(tenant.IsAuthenticated);
        Assert.False(tenant.HasTenant);
        Assert.False(tenant.IsPlatformAdmin);
    }

    [Fact]
    public void Tenant_comes_from_the_token()
    {
        var tenant = Resolve(Http([C("sub", "u1"), C("tenant_id", TenantA)]));

        Assert.True(tenant.IsAuthenticated);
        Assert.Equal(TenantA, tenant.TenantId);
        Assert.False(tenant.IsSubTenantScoped);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Invalid_tenant_claim_is_treated_as_no_tenant(string value)
    {
        var tenant = Resolve(Http([C("tenant_id", value)]));

        Assert.True(tenant.IsAuthenticated);
        Assert.False(tenant.HasTenant);
    }

    [Fact]
    public void Platform_admin_by_role()
    {
        var tenant = Resolve(Http([C("role", "PLATFORM_ADMIN")]));

        Assert.True(tenant.IsPlatformAdmin);
        Assert.False(tenant.HasTenant);
    }

    [Fact]
    public void Platform_admin_by_claim()
    {
        var tenant = Resolve(Http([C("is_platform_admin", "TRUE")]));

        Assert.True(tenant.IsPlatformAdmin);
    }

    [Fact]
    public void Platform_admin_claim_can_be_disabled()
    {
        var tenant = Resolve(Http([C("is_platform_admin", "true")]), Options(o => o.PlatformAdminClaimType = null));

        Assert.False(tenant.IsPlatformAdmin);
    }

    [Fact]
    public void Admin_impersonation_scopes_to_one_tenant_and_drops_the_bypass()
    {
        var tenant = Resolve(Http([C("role", "PLATFORM_ADMIN")], header: TenantB.ToString()));

        Assert.Equal(TenantB, tenant.TenantId);
        Assert.False(tenant.IsPlatformAdmin);
        Assert.True(tenant.IsImpersonating);
    }

    [Fact]
    public void Non_admin_cannot_impersonate()
    {
        var tenant = Resolve(Http([C("tenant_id", TenantA)], header: TenantB.ToString()));

        Assert.Equal(TenantA, tenant.TenantId);
        Assert.False(tenant.IsImpersonating);
    }

    [Fact]
    public void Invalid_impersonation_header_is_ignored()
    {
        var tenant = Resolve(Http([C("role", "PLATFORM_ADMIN")], header: "nope"));

        Assert.True(tenant.IsPlatformAdmin);
        Assert.False(tenant.HasTenant);
    }

    [Fact]
    public void Impersonation_can_be_disabled()
    {
        var tenant = Resolve(Http([C("role", "PLATFORM_ADMIN")], header: TenantB.ToString()),
            Options(o => o.ImpersonationHeader = null));

        Assert.True(tenant.IsPlatformAdmin);
        Assert.False(tenant.IsImpersonating);
    }

    [Fact]
    public void Sub_tenant_scoped_role_is_restricted_to_its_sub_tenants()
    {
        var tenant = Resolve(Http([C("tenant_id", TenantA), C("role", "BRANCH_MANAGER"),
            C("sub_tenant_id", Branch1), C("sub_tenant_id", Branch2), C("sub_tenant_id", Branch1),
            C("sub_tenant_id", "junk")]));

        Assert.True(tenant.IsSubTenantScoped);
        Assert.Equal([Branch1, Branch2], tenant.SubTenantIds);
    }

    [Fact]
    public void Tenant_admin_role_overrides_sub_tenant_scoping()
    {
        var tenant = Resolve(Http([C("tenant_id", TenantA), C("role", "BRANCH_MANAGER"),
            C("role", "TENANT_ADMIN"), C("sub_tenant_id", Branch1)]));

        Assert.False(tenant.IsSubTenantScoped);
        Assert.Equal([Branch1], tenant.SubTenantIds);
    }

    [Fact]
    public void Claim_types_are_configurable()
    {
        var tenant = Resolve(Http([C("CompanyId", TenantA)]), Options(o => o.TenantIdClaimType = "CompanyId"));

        Assert.Equal(TenantA, tenant.TenantId);
    }

    [Fact]
    public void Background_job_can_set_the_tenant_once()
    {
        var tenant = new TenantContext();
        tenant.SetTenant(TenantA, [Branch1]);

        Assert.Equal(TenantA, tenant.TenantId);
        Assert.True(tenant.IsSubTenantScoped);
        Assert.Throws<InvalidOperationException>(() => tenant.SetTenant(TenantB));
        Assert.Throws<ArgumentException>(() => new TenantContext().SetTenant(Guid.Empty));
    }
}
