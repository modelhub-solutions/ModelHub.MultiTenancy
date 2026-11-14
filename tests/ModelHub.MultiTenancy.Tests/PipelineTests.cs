using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ModelHub.MultiTenancy.Tests;

/// <summary>Signs the request in with the tenant given in the X-Test-Tenant header.</summary>
public class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Tenant", out var tenant))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim("tenant_id", tenant.ToString())], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

public class PipelineTests
{
    private record TenantResponse(Guid? TenantId, bool IsAuthenticated);

    [Fact]
    public async Task Registered_services_resolve_the_tenant_per_request()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("test", null);
                    services.AddMultiTenancy();
                })
                .Configure(app => app
                    .UseRouting()
                    .UseAuthentication()
                    .UseTenantResolution()
                    .UseEndpoints(e => e.MapGet("/tenant",
                        (ITenantContext t) => new TenantResponse(t.TenantId, t.IsAuthenticated)))))
            .StartAsync();

        var client = host.GetTestClient();
        var tenantA = Guid.NewGuid();

        var anonymous = await client.GetFromJsonAsync<TenantResponse>("/tenant");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenantA.ToString());
        var signedIn = await client.GetFromJsonAsync<TenantResponse>("/tenant");

        Assert.Equal(new TenantResponse(null, false), anonymous);
        Assert.Equal(new TenantResponse(tenantA, true), signedIn);
    }
}
