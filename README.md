# ModelHub.MultiTenancy

Lightweight, **claim-based multi-tenancy for ASP.NET Core and EF Core**.
The tenant comes from the signed-in user's token, and one line keeps every query and every save inside that tenant.

Extracted from [StrideWell](https://stridewell.app), a multi-tenant SaaS platform for gyms and coaches, where it isolates each business's data in production.

```
dotnet add package ModelHub.MultiTenancy
dotnet add package ModelHub.MultiTenancy.EntityFrameworkCore
```

Targets .NET 8 and .NET 10.

## Why another multi-tenancy library?

[Finbuckle.MultiTenant](https://github.com/Finbuckle/Finbuckle.MultiTenant) is excellent if you resolve tenants from hosts, routes or a tenant store.
This library is for the common SaaS case where **the user's token already says which tenant they belong to**, and you want:

- **No tenant store or strategy to configure.** The tenant id is a claim.
- **Two levels: tenant → sub-tenant** (company → branch, school → class). Branch managers see only their branches; tenant admins see everything.
- **Audited platform-admin impersonation.** A support admin sends `X-Tenant-Id` to act as one tenant. Their cross-tenant bypass is switched off for that request, and it is logged. Anyone else sending the header is ignored and logged.
- **Write protection, not just read filtering.** New rows get the tenant stamped automatically. Inserts, updates, deletes and "moves" that touch another tenant's data throw `TenantMismatchException`.
- **Safe defaults.** No tenant means no tenant rows. A bad claim is treated as no tenant.

## Quick start

### 1. Register and add the middleware

```csharp
builder.Services.AddAuthentication(/* your JWT setup */);
builder.Services.AddMultiTenancy(o =>
{
    o.TenantIdClaimType = "tenant_id";          // default
    o.PlatformAdminRoles.Add("platform_admin");
    o.TenantAdminRoles.Add("tenant_admin");
    o.SubTenantScopedRoles.Add("branch_manager");
});

var app = builder.Build();
app.UseAuthentication();
app.UseTenantResolution();   // after authentication, before authorization
app.UseAuthorization();
```

Inject `ITenantContext` anywhere you need the current tenant:

```csharp
app.MapGet("/me", (ITenantContext t) => new { t.TenantId, t.IsPlatformAdmin, t.SubTenantIds });
```

### 2. Mark your entities

```csharp
public class Invoice : ITenantEntity
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }      // stamped automatically on insert
}

public class Booking : ITenantEntity, ISubTenantEntity
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? SubTenantId { get; set; }  // the branch
}
```

### 3. Make the DbContext tenant-aware

```csharp
public class AppDb(DbContextOptions<AppDb> options, ITenantContext tenantContext)
    : DbContext(options), ITenantAwareDbContext
{
    public ITenantContext TenantContext { get; } = tenantContext;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // your configuration, including any other HasQueryFilter calls...
        modelBuilder.ApplyTenantQueryFilters(this);   // last
    }
}

builder.Services.AddDbContext<AppDb>(o => o.UseSqlServer(conn).UseMultiTenancy());
```

Every query on a tenant entity now runs as `IsPlatformAdmin || TenantId == @currentTenant`. One cached EF model serves all tenants.

### 4. Sub-tenant scoping (optional)

```csharp
var bookings = await db.Bookings
    .WhereVisibleToSubTenants(tenant, includeTenantWide: true)
    .ToListAsync();
```

Unscoped users (tenant admins, platform admins) get the query unchanged.

## Background jobs

There is no HTTP request in a job, so set the tenant on the scoped context yourself:

```csharp
using var scope = services.CreateScope();
scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
var db = scope.ServiceProvider.GetRequiredService<AppDb>();
```

`TenantContext.ForTenant(id)` and `TenantContext.ForPlatformAdmin()` create standalone contexts for tests and maintenance work.
A context can only be set once per scope, so one tenant's job can't silently switch to another.

## Options

| Option | Default | Meaning |
|---|---|---|
| `TenantIdClaimType` | `tenant_id` | Claim holding the tenant GUID |
| `SubTenantIdClaimType` | `sub_tenant_id` | Claim (repeatable) holding sub-tenant GUIDs |
| `PlatformAdminClaimType` | `is_platform_admin` | Claim that marks a platform admin when `true`; `null` to disable |
| `PlatformAdminRoles` | empty | Roles that bypass tenant filters |
| `TenantAdminRoles` | empty | Roles that always see the whole tenant |
| `SubTenantScopedRoles` | empty | Roles restricted to their sub-tenants |
| `ImpersonationHeader` | `X-Tenant-Id` | Header platform admins use to act as a tenant; `null` to disable |

## EF Core notes

- **EF Core 10+:** the filter is added as the named filter `ModelHub.MultiTenancy.Tenant`, alongside your other filters.
- **EF Core 8:** an entity can have only one filter. `ApplyTenantQueryFilters` combines with a filter already on the entity (e.g. soft delete), so call it **after** your own `HasQueryFilter` calls.
- Filters go on root entity types only (EF Core requirement). Owned types are skipped.
- `IgnoreQueryFilters()` bypasses tenant filtering. Treat it like `sudo`.
- DbContext pooling: the context must get the request's `ITenantContext`, so use a pooled factory that sets it, or don't pool.

## Try the sample

```
cd samples/ModelHub.MultiTenancy.Sample
dotnet run -f net8.0 --urls http://localhost:5199
```

```bash
U=http://localhost:5199
token() { curl -s -X POST $U/dev/token -H 'Content-Type: application/json' -d "$1" | sed -E 's/.*"token":"([^"]+)".*/\1/'; }

ACME=$(token '{"tenantId":"a0000000-0000-0000-0000-000000000001","roles":["tenant_admin"]}')
CARDIFF=$(token '{"tenantId":"a0000000-0000-0000-0000-000000000001","roles":["branch_manager"],"subTenantIds":["a0000000-0000-0000-0000-0000000000c1"]}')
ADMIN=$(token '{"roles":["platform_admin"]}')

curl $U/notes -H "Authorization: Bearer $ACME"       # both Acme branches
curl $U/notes -H "Authorization: Bearer $CARDIFF"    # Cardiff only
curl $U/notes -H "Authorization: Bearer $ADMIN" -H "X-Tenant-Id: b0000000-0000-0000-0000-000000000002"   # acts as Globex
```

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). Issues labelled `good first issue` are a good place to start.

## Licence

MIT © ModelHub Solutions Ltd
