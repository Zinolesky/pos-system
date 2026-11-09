using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PosSystem.Shared.Models;

namespace PosSystem.Backend.Data;

public class BackendDbContext : DbContext
{
    public BackendDbContext(DbContextOptions<BackendDbContext> options) : base(options) { }

    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<Category> Categories { get; set; } = null!;
    public DbSet<TaxRate> TaxRates { get; set; } = null!;
    public DbSet<Sale> Sales { get; set; } = null!;
    public DbSet<SaleLineItem> SaleLineItems { get; set; } = null!;
    public DbSet<Cashier> Cashiers { get; set; } = null!;
    public DbSet<Customer> Customers { get; set; } = null!;
    public DbSet<ShiftSession> ShiftSessions { get; set; } = null!;
    public DbSet<InventoryMovement> InventoryMovements { get; set; } = null!;
    public DbSet<StoreSetting> StoreSettings { get; set; } = null!;
    public DbSet<AuditLog> AuditLogs { get; set; } = null!;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder
            .Properties<Ulid>()
            .HaveConversion<UlidToGuidConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SaleLineItem>()
            .HasOne(ti => ti.Sale)
            .WithMany(t => t.Items)
            .HasForeignKey(ti => ti.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Product>()
            .HasOne(p => p.Category)
            .WithMany()
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Cashier>()
            .HasIndex(c => c.Username)
            .IsUnique();

        modelBuilder.Entity<Customer>()
            .HasIndex(c => c.Phone);

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Sku);

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Barcode);

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.ReceiptNumber);

        modelBuilder.Entity<StoreSetting>()
            .HasIndex(s => s.Key)
            .IsUnique();

        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.CreatedAt);
    }
}

public class BackendDesignTimeDbContextFactory : IDesignTimeDbContextFactory<BackendDbContext>
{
    public BackendDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<BackendDbContext>();
        var conn = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                   ?? "Host=localhost;Port=5433;Database=PosBackend;Username=pos_admin;Password=DesignTimeOnly";

        optionsBuilder
            .UseNpgsql(conn)
            .UseSnakeCaseNamingConvention();

        return new BackendDbContext(optionsBuilder.Options);
    }
}
