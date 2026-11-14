# Changelog

## 0.1.1 (14 Nov 2026)

- Fuller NuGet description for ModelHub.MultiTenancy.EntityFrameworkCore, with a worked example

## 0.1.0 (14 Nov 2026)

First public release, extracted from the StrideWell API.

- `ITenantContext` / `TenantContext`: per-request tenant, sub-tenants, platform admin and impersonation state
- `TenantResolutionMiddleware`: reads the tenant from claims; configurable claim types and roles
- Audited platform-admin impersonation via `X-Tenant-Id` (configurable, can be disabled)
- `ApplyTenantQueryFilters`: tenant filter on every `ITenantEntity` (named filter on EF Core 10, combined filter on EF Core 8)
- `TenantSaveChangesInterceptor`: stamps the tenant on insert; blocks cross-tenant insert, update, move and delete
- `WhereVisibleToSubTenants`: sub-tenant scoping for queries
- Sample minimal API with JWT, and a test suite on .NET 8 and .NET 10
