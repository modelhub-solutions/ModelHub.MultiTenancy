using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelHub.MultiTenancy.EntityFrameworkCore;

namespace ModelHub.MultiTenancy.Tests;

/// <summary>
/// The platform-admin bypass leaves an audit trail: the middleware logs every all-tenant request,
/// and the save interceptor logs every write a platform admin makes.
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly DbContextOptions<TestDbContext> _options;

    public AuditLogTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Information));
        _connection.Open();
        _options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection)
            .UseLoggerFactory(_loggerFactory)
            .UseMultiTenancy()
            .Options;

        using var tenantA = Db(TenantContext.ForTenant(TenantA));
        tenantA.Database.EnsureCreated();
        tenantA.Notes.Add(new Note { Text = "A1" });
        tenantA.SaveChanges();
        using var tenantB = Db(TenantContext.ForTenant(TenantB));
        tenantB.Notes.Add(new Note { Text = "B1" });
        tenantB.SaveChanges();
        _logs.Clear();
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _connection.Dispose();
    }

    private TestDbContext Db(ITenantContext tenant) => new(_options, tenant);

    private string[] AuditMessages() => _logs.Messages(typeof(TenantSaveChangesInterceptor).FullName!);

    // ── Save interceptor ───────────────────────────────────────────────────

    [Fact]
    public void Platform_admin_add_update_and_delete_are_logged_with_the_tenant()
    {
        using var admin = Db(TenantContext.ForPlatformAdmin());
        var b1 = admin.Notes.Single(n => n.Text == "B1");
        var a1 = admin.Notes.Single(n => n.Text == "A1");
        b1.Text = "B1 edited by support";
        admin.Notes.Remove(a1);
        admin.Notes.Add(new Note { TenantId = TenantB, Text = "added by support" });
        admin.SaveChanges();

        var messages = AuditMessages();
        Assert.Equal(3, messages.Length);
        Assert.Contains($"Platform admin is updating Note {b1.Id} in tenant {TenantB}.", messages);
        Assert.Contains($"Platform admin is deleting Note {a1.Id} in tenant {TenantA}.", messages);
        Assert.Contains($"Platform admin is adding Note (new) in tenant {TenantB}.", messages);
    }

    [Fact]
    public async Task Platform_admin_moving_a_row_logs_both_tenants()
    {
        using var admin = Db(TenantContext.ForPlatformAdmin());
        var note = admin.Notes.Single(n => n.Text == "A1");
        note.TenantId = TenantB;
        await admin.SaveChangesAsync();

        Assert.Equal(
            [$"Platform admin is moving Note {note.Id} from tenant {TenantA} to tenant {TenantB}."],
            AuditMessages());
    }

    [Fact]
    public void Writes_by_a_tenant_user_are_not_audit_logged()
    {
        using var db = Db(TenantContext.ForTenant(TenantA));
        db.Notes.Single().Text = "edited";
        db.Notes.Add(new Note { Text = "A2" });
        db.SaveChanges();

        Assert.Empty(AuditMessages());
    }

    [Fact]
    public void Refused_admin_write_is_not_logged_as_a_write()
    {
        using var admin = Db(TenantContext.ForPlatformAdmin());
        admin.Notes.Add(new Note { Text = "whose?" }); // no TenantId: refused

        Assert.Throws<TenantMismatchException>(() => admin.SaveChanges());
        Assert.Empty(AuditMessages());
    }

    // ── Middleware ─────────────────────────────────────────────────────────

    private (TenantContext Tenant, string[] Messages) Resolve(IEnumerable<Claim> claims, string? header = null)
    {
        var options = new MultiTenancyOptions();
        options.PlatformAdminRoles.Add("PLATFORM_ADMIN");
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask,
            Microsoft.Extensions.Options.Options.Create(options),
            _loggerFactory.CreateLogger<TenantResolutionMiddleware>());

        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "sub", "role"));
        http.Request.Method = "GET";
        http.Request.Path = "/api/notes";
        if (header is not null)
            http.Request.Headers["X-Tenant-Id"] = header;

        var tenant = new TenantContext();
        middleware.Resolve(http, tenant);
        return (tenant, _logs.Messages(typeof(TenantResolutionMiddleware).FullName!));
    }

    [Fact]
    public void Platform_admin_with_all_tenant_access_is_logged()
    {
        var (tenant, messages) = Resolve([new("sub", "admin-1"), new("role", "PLATFORM_ADMIN")]);

        Assert.True(tenant.IsPlatformAdmin);
        Assert.Equal(["Platform admin admin-1 has all-tenant access on GET /api/notes."], messages);
    }

    [Fact]
    public void Impersonating_admin_logs_the_impersonation_not_all_tenant_access()
    {
        var (tenant, messages) = Resolve(
            [new("sub", "admin-1"), new("role", "PLATFORM_ADMIN")], header: TenantA.ToString());

        Assert.False(tenant.IsPlatformAdmin);
        Assert.Equal([$"Platform admin admin-1 is acting as tenant {TenantA} on /api/notes."], messages);
    }

    [Fact]
    public void Ordinary_user_request_is_not_logged()
    {
        var (_, messages) = Resolve([new("sub", "user-1"), new("tenant_id", TenantA.ToString())]);

        Assert.Empty(messages);
    }
}

/// <summary>Collects formatted log messages per category for assertions.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<(string Category, string Message)> _entries = [];

    public string[] Messages(string category)
    {
        lock (_entries)
            return _entries.Where(e => e.Category == category).Select(e => e.Message).ToArray();
    }

    public void Clear()
    {
        lock (_entries)
            _entries.Clear();
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._entries)
                owner._entries.Add((category, formatter(state, exception)));
        }
    }
}
