# Design note 0001: tenant-scoped MCP servers (`ModelHub.MultiTenancy.Mcp`)

| | |
|---|---|
| **Status** | Draft, 21 November 2026. Not yet discussed in public |
| **Target** | `0.2.0-preview.1` in December 2026; `0.2.0` in January 2027, after early feedback |
| **Author** | Kehinde Olosunde |
| **Depends on** | `ModelHub.MultiTenancy` 0.1.3+, `ModelContextProtocol.AspNetCore` 2.x (2.4.0 at the time of writing; targets net8.0, net9.0 and net10.0) |

## Summary

A small package that makes an ASP.NET Core MCP server respect the same tenant boundary as the rest of
the app. Every MCP request runs with the tenant taken from the caller's access token, through the
existing `ITenantContext`, so the EF Core query filter and `SaveChanges` interceptor apply to tool code
unchanged. It adds three MCP-specific protections: no tenant from tool arguments, no all-tenant tokens
by default, and an audit line for every tool call.

## The problem

MCP servers over Streamable HTTP now often serve many organisations from one deployment. Tenant
isolation that works for a REST API has three new ways to fail:

1. **The model writes the tool arguments.** If a tool takes `tenantId` (or anything that names a
   tenant) as an argument, a prompt injection, or a confused model, can ask for another tenant's data
   with a perfectly valid token. *The tenant must come from the token, never from the arguments.*
2. **Work can outlive the request.** Stateful sessions are created by one HTTP request and used by
   later ones, and task-backed tool calls finish in the background after the request has ended. The
   tenant has to travel with the work, not be read from a request that no longer exists.
3. **Agents with admin tokens.** An AI client holding a platform-admin token can reach every tenant.
   That is a much larger blast radius than a person using an admin screen, and the 0.1.3 audit log only
   records it after the fact.

## Goals

- Every MCP request (`tools/list`, `tools/call`, `resources/*`, `prompts/*`) runs with the tenant
  resolved from the caller's token by the existing `TenantResolutionMiddleware`.
- **Fail closed.** A request with no tenant is rejected before any handler runs.
- Platform admins are **refused** on MCP endpoints by default, and the impersonation header
  (`X-Tenant-Id`) is **ignored** there by default. Both can be turned on explicitly, and both are
  audited when they are.
- Tools whose input names a tenant are **caught at startup**, not in production.
- One **audit line per tool call**: tenant, user, tool, outcome. Never the arguments or the result,
  which may hold personal data.
- Works with the SDK's default **stateless** mode. Stateful (legacy `initialize`) clients are documented
  and tested, or refused.

## Non-goals

- **stdio transport.** A local, single-user server has no tenants to separate.
- **Being an authorisation server.** Use ASP.NET Core authentication and the SDK's `AddMcp(...)`
  protected-resource metadata; this package only reads the validated principal.
- **Per-user authorisation inside a tenant.** The library guards the tenant boundary. Which rows *one
  user* may see is still the app's job (see the example below).
- **Per-tenant tool catalogues** in 0.2. Possible later with a `tools/list` filter.

## How it fits the current library

Today: `UseAuthentication()` → `UseTenantResolution()` → scoped `TenantContext` → EF Core query filter
and `TenantSaveChangesInterceptor`.

MCP over Streamable HTTP sends each JSON-RPC message as an HTTP POST. In stateless mode, which is the
SDK default since the `2026-07-28` protocol revision removed sessions, each request runs in its own
scope, and a filter's `context.Services` is that request's service provider. So the `TenantContext` the
middleware fills is the one tool code receives. That is why 0.2 supports stateless first.

## Proposed API

```csharp
builder.Services.AddMultiTenancy(o =>
{
    o.PlatformAdminRoles.Add("PLATFORM_ADMIN");
});

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithTenantScope(o =>
    {
        o.AllowPlatformAdmin = false;            // default: refuse all-tenant tokens
        o.AllowImpersonationHeader = false;      // default: X-Tenant-Id ignored on MCP
        o.OnTenantParameter = TenantParameterAction.Throw;   // Throw (default) or Warn
        o.TenantParameterNames.Add("organisationId");        // added to tenantId, tenant_id, tenant
        o.AuditToolCalls = true;                 // default
    })
    .WithToolsFromAssembly();

var app = builder.Build();
app.UseAuthentication();
app.UseTenantResolution();   // unchanged: after authentication, before authorization
app.UseAuthorization();
app.MapMcp("/mcp").RequireAuthorization();
```

### What `WithTenantScope` does

1. **Request filters** (`WithRequestFilters`: list/call tools, list/read resources, list/get prompts)
   read `ITenantContext` from `context.Services` and reject the request with an MCP error unless it is
   authenticated with a tenant. Platform admins are rejected unless `AllowPlatformAdmin` is set.
2. **Impersonation off on MCP.** The middleware gains an option to skip the impersonation header for
   the MCP endpoint, so a header the client sends can't switch tenants.
3. **Startup check of tool inputs.** After the server options are built, every registered tool's input
   schema is inspected. A property matching `TenantParameterNames` (case-insensitive) throws
   `InvalidOperationException` naming the tool and the parameter, or logs a warning.
4. **Audit.** An ordinary call-tool filter logs, at Information level under the category
   `ModelHub.MultiTenancy.Mcp`:
   `MCP tool {Tool} called by {UserId} in tenant {TenantId}: {Outcome}.`
   This matches the 0.1.3 audit style, so it lands wherever the app already ships audit logs.
5. **Work outside the request.** For task-backed calls, a snapshot of the tenant is taken when the call
   starts and restored in the scope where the tool runs. If that can't be done reliably with the SDK's
   Tasks extension, 0.2 **refuses** task-backed calls when tenant scoping is on, and says so in the docs.

## What tool code looks like

The vet-practice example from the write-up: a practice is a tenant and each clinic is a sub-tenant.
The tool has no tenant parameter and no tenant code at all. The query filter limits it to the caller's
practice, and to their own clinic when their role is sub-tenant scoped (a receptionist, for example).

```csharp
[McpServerToolType]
public sealed class AppointmentTools(VetDbContext db)
{
    [McpServerTool(ReadOnly = true), Description("Today's appointments at the caller's practice.")]
    public async Task<IReadOnlyList<AppointmentDto>> GetTodaysAppointments(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await db.Appointments
            .Where(a => a.Date == today)        // tenant and clinic: added by the query filter
            .OrderBy(a => a.StartsAt)
            .Select(a => new AppointmentDto(a.StartsAt, a.PetName, a.Reason))
            .ToListAsync(ct);
    }
}
```

The library guards the boundary between tenants. If an app puts many unrelated users in one tenant,
limiting each user to their own rows is still the app's job.

## Tests (acceptance criteria)

Same approach as `AuditLogTests`: SQLite in memory, two tenants, an in-process MCP client.

- A tool called with a tenant A token sees only tenant A rows.
- A token with no tenant claim is rejected, and the tool is never invoked.
- A platform-admin token is rejected by default; with `AllowPlatformAdmin = true` it works and is audited.
- `X-Tenant-Id` sent by an MCP client has no effect by default.
- Registering a tool with a `tenantId` parameter fails at startup with a message naming the tool.
- Each call writes one audit line, with no arguments in it.
- A task-backed call keeps its tenant, or is refused.
- Stateful mode: a tenant B token can't use a session that tenant A started.
- CI on net8.0 and net10.0.

## Packaging and versioning

- New package `ModelHub.MultiTenancy.Mcp`, starting at `0.2.0-preview.1`. The core packages don't
  change.
- README section, a sample (`samples/Mcp.VetPractice`, reusing the vet-practice example from the
  write-up), and a CHANGELOG entry.
- Published by the existing tag-triggered Trusted Publishing workflow.

## Open questions

1. In `StatefulForInitializeClients` mode, does each request still see its own `ITenantContext`, or the
   one from the request that started the session? Needs a test before stateful is supported.
2. Can a tenant snapshot flow into the Tasks extension's background execution cleanly, or should
   task-backed calls simply be refused?
3. Tool-level scopes (read vs write): document the SDK's ASP.NET Core authorisation support for tools,
   or add a helper here?
4. Resource URIs: reject any that carry a tenant identifier, like tool arguments?
5. Package name: `.Mcp` or `.ModelContextProtocol`?

## Plan

| When | Step |
|---|---|
| Early Dec 2026 | Publish this note as a GitHub issue and ask for feedback |
| Mid Dec 2026 | Filters, startup check, audit and tests; `0.2.0-preview.1` |
| Dec 2026–Jan 2027 | Raise issues or PRs on `modelcontextprotocol/csharp-sdk` for any gaps found (e.g. per-request services in stateful mode, Tasks) |
| Jan 2027 | Ship the vet-practice MCP sample; ask early users for feedback in the issue; fix what they find; `0.2.0` |
| Feb 2027 | Write-up: "Multi-tenant MCP servers without leaking data" |
