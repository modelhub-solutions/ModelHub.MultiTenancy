// A minimal API showing ModelHub.MultiTenancy end to end:
//   POST /dev/token  -> issue a test JWT for any tenant/role (development only)
//   GET  /me         -> what the tenant context resolved
//   GET  /notes      -> notes for the current tenant (and sub-tenants, if scoped)
//   POST /notes      -> add a note; the tenant is stamped automatically
// See README.md for curl commands.

using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelHub.MultiTenancy;
using ModelHub.MultiTenancy.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Development-only signing key. Never ship a hard-coded key.
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("sample-only-signing-key-change-me-0123456789"));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            IssuerSigningKey = signingKey,
            NameClaimType = "sub",
            RoleClaimType = "role",
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddMultiTenancy(o =>
{
    o.PlatformAdminRoles.Add("platform_admin");
    o.TenantAdminRoles.Add("tenant_admin");
    o.SubTenantScopedRoles.Add("branch_manager");
});

// One in-memory SQLite database shared for the life of the app.
var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
builder.Services.AddDbContext<NotesDb>(o => o.UseSqlite(connection).UseMultiTenancy());

var app = builder.Build();

app.UseAuthentication();
app.UseTenantResolution(); // after authentication, before authorization
app.UseAuthorization();

// Seed two tenants as a platform admin.
using (var scope = app.Services.CreateScope())
{
    var db = new NotesDb(scope.ServiceProvider.GetRequiredService<DbContextOptions<NotesDb>>(), TenantContext.ForPlatformAdmin());
    db.Database.EnsureCreated();
    db.Notes.AddRange(
        new Note { TenantId = Ids.Acme, SubTenantId = Ids.AcmeCardiff, Text = "Acme Cardiff: new member induction" },
        new Note { TenantId = Ids.Acme, SubTenantId = Ids.AcmeBristol, Text = "Acme Bristol: boiler service Friday" },
        new Note { TenantId = Ids.Globex, Text = "Globex: quarterly review" });
    db.SaveChanges();
}

app.MapPost("/dev/token", (TokenRequest request) =>
{
    var claims = new List<Claim> { new("sub", request.User ?? "demo-user") };
    if (request.TenantId is { } tenant) claims.Add(new("tenant_id", tenant.ToString()));
    claims.AddRange((request.Roles ?? []).Select(r => new Claim("role", r)));
    claims.AddRange((request.SubTenantIds ?? []).Select(s => new Claim("sub_tenant_id", s.ToString())));

    var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(claims),
        Expires = DateTime.UtcNow.AddHours(1),
        SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
    });
    return Results.Ok(new { token });
});

app.MapGet("/me", (ITenantContext t) => new
{
    t.TenantId, t.IsAuthenticated, t.IsPlatformAdmin, t.IsImpersonating, t.IsSubTenantScoped, t.SubTenantIds,
});

app.MapGet("/notes", async (NotesDb db, ITenantContext tenant) =>
    await db.Notes.WhereVisibleToSubTenants(tenant, includeTenantWide: true)
        .OrderBy(n => n.Id).ToListAsync())
    .RequireAuthorization();

app.MapPost("/notes", async (NotesDb db, NewNote input) =>
{
    var note = new Note { Text = input.Text, SubTenantId = input.SubTenantId };
    db.Notes.Add(note); // TenantId is stamped by the interceptor
    try
    {
        await db.SaveChangesAsync();
        return Results.Created($"/notes/{note.Id}", note);
    }
    catch (TenantMismatchException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization();

app.Run();

static class Ids
{
    public static readonly Guid Acme = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid AcmeCardiff = Guid.Parse("a0000000-0000-0000-0000-0000000000c1");
    public static readonly Guid AcmeBristol = Guid.Parse("a0000000-0000-0000-0000-0000000000b1");
    public static readonly Guid Globex = Guid.Parse("b0000000-0000-0000-0000-000000000002");
}

record TokenRequest(string? User, Guid? TenantId, string[]? Roles, Guid[]? SubTenantIds);
record NewNote(string Text, Guid? SubTenantId);

class Note : ITenantEntity, ISubTenantEntity
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? SubTenantId { get; set; }
    public string Text { get; set; } = "";
}

class NotesDb(DbContextOptions<NotesDb> options, ITenantContext tenantContext) : DbContext(options), ITenantAwareDbContext
{
    public ITenantContext TenantContext { get; } = tenantContext;
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyTenantQueryFilters(this);
}
