using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PosSystem.Shared.Data;
using PosSystem.Shared.Models;

namespace PosSystem.Frontend.Data;

public class LocalDbContext : DbContext
{
    public const string CurrentSchemaVersion = "2.2";
    public const string SchemaVersionSettingKey = "LocalSqliteSchemaVersion";

    public static string GetDatabasePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PosSystem");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "pos_local.db");
    }

    public LocalDbContext(DbContextOptions<LocalDbContext> options) : base(options)
    {
    }

    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<Category> Categories { get; set; } = null!;
    public DbSet<TaxRate> TaxRates { get; set; } = null!;
    public DbSet<Cashier> Cashiers { get; set; } = null!;
    public DbSet<Customer> Customers { get; set; } = null!;
    public DbSet<Sale> Sales { get; set; } = null!;
    public DbSet<SaleLineItem> SaleLineItems { get; set; } = null!;
    public DbSet<ShiftSession> ShiftSessions { get; set; } = null!;
    public DbSet<InventoryMovement> InventoryMovements { get; set; } = null!;
    public DbSet<SyncQueue> SyncQueues { get; set; } = null!;
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

        modelBuilder.Entity<SyncQueue>()
            .HasOne(sq => sq.Sale)
            .WithMany()
            .HasForeignKey(sq => sq.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Cashier>()
            .HasIndex(c => c.Username)
            .IsUnique();

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Sku);

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Barcode);

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.ReceiptNumber);

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.CreatedAt);

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.Status);

        modelBuilder.Entity<StoreSetting>()
            .HasIndex(s => s.Key)
            .IsUnique();

        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.CreatedAt);
    }

    public void InitializeAndSeed()
    {
        Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -20000;");

        // Fast-path check: if schema v2.2 stamp already exists and full entity projection succeeds, return immediately
        try
        {
            var versionStamp = StoreSettings
                .AsNoTracking()
                .FirstOrDefault(s => s.Key == SchemaVersionSettingKey);

            if (versionStamp?.Value == CurrentSchemaVersion && Products.AsNoTracking().Any())
            {
                _ = Sales.AsNoTracking().Include(s => s.Items).FirstOrDefault();
                return;
            }
        }
        catch
        {
            // Proceed with non-destructive schema upgrade below
        }

        try
        {
            Database.EnsureCreated();
            EnsureSqliteSchemaUpgraded();

            // Verify full entity projections across all tables including Sale + SaleLineItems
            _ = Products.AsNoTracking().FirstOrDefault();
            _ = Categories.AsNoTracking().FirstOrDefault();
            _ = TaxRates.AsNoTracking().FirstOrDefault();
            _ = Cashiers.AsNoTracking().FirstOrDefault();
            _ = Customers.AsNoTracking().FirstOrDefault();
            _ = ShiftSessions.AsNoTracking().FirstOrDefault();
            _ = InventoryMovements.AsNoTracking().FirstOrDefault();
            _ = StoreSettings.AsNoTracking().FirstOrDefault();
            _ = AuditLogs.AsNoTracking().FirstOrDefault();
            _ = Sales.AsNoTracking().Include(s => s.Items).FirstOrDefault();
            _ = SaleLineItems.AsNoTracking().FirstOrDefault();
        }
        catch
        {
            Database.EnsureDeleted();
            Database.EnsureCreated();
        }

        if (!TaxRates.Any())
            TaxRates.AddRange(CatalogSeedData.GetDefaultTaxRates());

        if (!Categories.Any())
            Categories.AddRange(CatalogSeedData.GetDefaultCategories());

        if (!Products.Any())
            Products.AddRange(CatalogSeedData.GetDefaultProducts());

        if (!Cashiers.Any())
            Cashiers.AddRange(CatalogSeedData.GetDefaultCashiers());

        if (!Customers.Any())
            Customers.AddRange(CatalogSeedData.GetDefaultCustomers());

        if (!StoreSettings.Any(s => s.Key == StoreSetting.InventoryPolicyKey))
            StoreSettings.AddRange(CatalogSeedData.GetDefaultStoreSettings());

        var existingStamp = StoreSettings.FirstOrDefault(s => s.Key == SchemaVersionSettingKey);
        if (existingStamp == null)
        {
            StoreSettings.Add(new StoreSetting
            {
                Id = Ulid.NewUlid(),
                Key = SchemaVersionSettingKey,
                Value = CurrentSchemaVersion,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existingStamp.Value = CurrentSchemaVersion;
            existingStamp.UpdatedAt = DateTime.UtcNow;
        }

        SaveChanges();
    }

    private void EnsureSqliteSchemaUpgraded()
    {
        Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""StoreSettings"" (
                ""Id"" TEXT NOT NULL CONSTRAINT ""PK_StoreSettings"" PRIMARY KEY,
                ""Key"" TEXT NOT NULL,
                ""Value"" TEXT NOT NULL,
                ""UpdatedAt"" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_StoreSettings_Key"" ON ""StoreSettings"" (""Key"");

            CREATE TABLE IF NOT EXISTS ""AuditLogs"" (
                ""Id"" TEXT NOT NULL CONSTRAINT ""PK_AuditLogs"" PRIMARY KEY,
                ""TerminalId"" TEXT NOT NULL,
                ""ActionType"" TEXT NOT NULL,
                ""Description"" TEXT NOT NULL,
                ""PerformedById"" TEXT NULL,
                ""PerformedByName"" TEXT NOT NULL,
                ""AuthorizedById"" TEXT NULL,
                ""AuthorizedByName"" TEXT NOT NULL,
                ""ReferenceId"" TEXT NULL,
                ""IsSynced"" INTEGER NOT NULL DEFAULT 0,
                ""CreatedAt"" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_AuditLogs_CreatedAt"" ON ""AuditLogs"" (""CreatedAt"");
            CREATE INDEX IF NOT EXISTS ""IX_Sales_CreatedAt"" ON ""Sales"" (""CreatedAt"");
            CREATE INDEX IF NOT EXISTS ""IX_Sales_Status"" ON ""Sales"" (""Status"");
        ");

        // Explicit column upgrades for Sales & SaleLineItems
        EnsureColumnExists("Sales", "IsTaxInclusive", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists("Sales", "RefundedAmount", "TEXT NOT NULL DEFAULT '0.0'");
        EnsureColumnExists("Sales", "OriginalSaleId", "TEXT NULL");
        EnsureColumnExists("Sales", "Notes", "TEXT NULL");
        EnsureColumnExists("SaleLineItems", "MeasuredWeightKg", "TEXT NULL");
        EnsureColumnExists("SaleLineItems", "IsNegativeStockAuthorized", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists("SaleLineItems", "ReturnedQuantity", "INTEGER NOT NULL DEFAULT 0");

        // Model-driven safety net: verify every EF Core entity property column exists on its SQLite table
        foreach (var entityType in Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrEmpty(tableName)) continue;

            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            foreach (var prop in entityType.GetProperties())
            {
                if (prop.IsPrimaryKey()) continue;
                var colName = prop.GetColumnName(storeObject);
                if (string.IsNullOrEmpty(colName)) continue;

                string colType = prop.GetColumnType(storeObject) ?? "TEXT";
                if (prop.IsNullable)
                {
                    EnsureColumnExists(tableName, colName, $"{colType} NULL");
                }
                else
                {
                    string defaultLiteral = colType.Contains("INT", StringComparison.OrdinalIgnoreCase)
                        ? "0"
                        : (prop.ClrType == typeof(decimal) || prop.ClrType == typeof(double) || prop.ClrType == typeof(float)
                            ? "'0.0'"
                            : "''");
                    EnsureColumnExists(tableName, colName, $"{colType} NOT NULL DEFAULT {defaultLiteral}");
                }
            }
        }
    }

    private void EnsureColumnExists(string tableName, string columnName, string columnDefinition)
    {
        var conn = Database.GetDbConnection();
        bool openedHere = false;
        if (conn.State != ConnectionState.Open)
        {
            conn.Open();
            openedHere = true;
        }

        try
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.FieldCount > 1 && !reader.IsDBNull(1))
                        existingColumns.Add(reader.GetString(1));
                }
            }

            if (existingColumns.Count > 0 && !existingColumns.Contains(columnName))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnDefinition};";
                alterCmd.ExecuteNonQuery();
            }
        }
        finally
        {
            if (openedHere)
                conn.Close();
        }
    }
}
