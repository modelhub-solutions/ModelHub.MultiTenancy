using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ModelHub.MultiTenancy.EntityFrameworkCore;

namespace ModelHub.MultiTenancy.Tests;

public class Note : ITenantEntity, ISubTenantEntity
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? SubTenantId { get; set; }
    public string Text { get; set; } = "";
    public bool IsDeleted { get; set; }
}

public class Country // not a tenant entity: never filtered
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class TestDbContext(DbContextOptions<TestDbContext> options, ITenantContext tenantContext)
    : DbContext(options), ITenantAwareDbContext
{
    public ITenantContext TenantContext { get; } = tenantContext;
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Country> Countries => Set<Country>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Note>().HasQueryFilter(
#if NET10_0_OR_GREATER
            "SoftDelete",
#endif
            n => !n.IsDeleted);
        modelBuilder.Entity<Country>();
        modelBuilder.ApplyTenantQueryFilters(this);
    }
}

public sealed class EntityFrameworkCoreTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Branch1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Branch2 = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<TestDbContext> _options;

    public EntityFrameworkCoreTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection)
            .UseMultiTenancy()
            .Options;

        using var admin = Db(TenantContext.ForPlatformAdmin());
        admin.Database.EnsureCreated();
        admin.Notes.AddRange(
            new Note { TenantId = TenantA, SubTenantId = Branch1, Text = "A1" },
            new Note { TenantId = TenantA, SubTenantId = Branch2, Text = "A2" },
            new Note { TenantId = TenantA, SubTenantId = null, Text = "A-wide" },
            new Note { TenantId = TenantA, Text = "A-deleted", IsDeleted = true },
            new Note { TenantId = TenantB, SubTenantId = null, Text = "B1" });
        admin.Countries.Add(new Country { Name = "Wales" });
        admin.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private TestDbContext Db(ITenantContext tenant) => new(_options, tenant);

    private static string[] Texts(IQueryable<Note> q) => q.OrderBy(n => n.Id).Select(n => n.Text).ToArray();

    [Fact]
    public void Each_tenant_sees_only_its_own_rows_from_the_same_cached_model()
    {
        using var a = Db(TenantContext.ForTenant(TenantA));
        using var b = Db(TenantContext.ForTenant(TenantB));

        Assert.Equal(["A1", "A2", "A-wide"], Texts(a.Notes));
        Assert.Equal(["B1"], Texts(b.Notes));
        Assert.Equal(["A1", "A2", "A-wide"], Texts(a.Notes)); // no leakage after switching
    }

    [Fact]
    public void Platform_admin_sees_every_tenant()
    {
        using var db = Db(TenantContext.ForPlatformAdmin());

        Assert.Equal(["A1", "A2", "A-wide", "B1"], Texts(db.Notes));
    }

    [Fact]
    public void No_tenant_sees_no_tenant_rows_but_still_sees_shared_tables()
    {
        using var db = Db(new TenantContext());

        Assert.Empty(db.Notes.ToList());
        Assert.Single(db.Countries.ToList());
    }

    [Fact]
    public void Existing_soft_delete_filter_is_kept()
    {
        using var db = Db(TenantContext.ForTenant(TenantA));

        Assert.DoesNotContain("A-deleted", Texts(db.Notes));
        Assert.Contains("A-deleted", db.Notes.IgnoreQueryFilters().Select(n => n.Text).ToList());
    }

    [Fact]
    public void New_rows_are_stamped_with_the_current_tenant()
    {
        using (var db = Db(TenantContext.ForTenant(TenantB)))
        {
            db.Notes.Add(new Note { Text = "B2" });
            db.SaveChanges();
        }

        using var check = Db(TenantContext.ForTenant(TenantB));
        Assert.Equal(["B1", "B2"], Texts(check.Notes));
    }

    [Fact]
    public void Adding_a_row_for_another_tenant_is_refused()
    {
        using var db = Db(TenantContext.ForTenant(TenantA));
        db.Notes.Add(new Note { TenantId = TenantB, Text = "sneaky" });

        Assert.Throws<TenantMismatchException>(() => db.SaveChanges());
    }

    [Fact]
    public void Adding_without_any_tenant_is_refused()
    {
        using var db = Db(TenantContext.ForPlatformAdmin());
        db.Notes.Add(new Note { Text = "whose?" });

        Assert.Throws<TenantMismatchException>(() => db.SaveChanges());
    }

    [Fact]
    public void Moving_a_row_to_another_tenant_is_refused()
    {
        using var db = Db(TenantContext.ForTenant(TenantA));
        var note = db.Notes.First();
        note.TenantId = TenantB;

        Assert.Throws<TenantMismatchException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task Deleting_another_tenants_row_by_attaching_it_is_refused()
    {
        int bId;
        using (var admin = Db(TenantContext.ForPlatformAdmin()))
            bId = admin.Notes.Single(n => n.Text == "B1").Id;

        using var db = Db(TenantContext.ForTenant(TenantA));
        db.Notes.Remove(new Note { Id = bId, TenantId = TenantB });

        await Assert.ThrowsAsync<TenantMismatchException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Sub_tenant_scoped_user_sees_only_their_sub_tenants()
    {
        var tenant = TenantContext.ForTenant(TenantA, [Branch1]);
        using var db = Db(tenant);

        Assert.Equal(["A1"], Texts(db.Notes.WhereVisibleToSubTenants(tenant)));
        Assert.Equal(["A1", "A-wide"], Texts(db.Notes.WhereVisibleToSubTenants(tenant, includeTenantWide: true)));
    }

    [Fact]
    public void Unscoped_user_is_not_restricted_by_sub_tenant()
    {
        var tenant = TenantContext.ForTenant(TenantA);
        using var db = Db(tenant);

        Assert.Equal(["A1", "A2", "A-wide"], Texts(db.Notes.WhereVisibleToSubTenants(tenant)));
    }
}
