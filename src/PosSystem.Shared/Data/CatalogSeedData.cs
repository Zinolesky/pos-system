using System;
using System.Collections.Generic;
using PosSystem.Shared.Models;
using PosSystem.Shared.Security;

namespace PosSystem.Shared.Data;

public static class CatalogSeedData
{
    private static Ulid DeterministicUlid(int group, int index)
        => new Ulid(new Guid(group, 0, 0, 0, 0, 0, 0, 0, 0, (byte)(index >> 8), (byte)(index & 0xFF)));

    public static readonly Ulid CatProduceId   = DeterministicUlid(1, 1);
    public static readonly Ulid CatBakeryId    = DeterministicUlid(1, 2);
    public static readonly Ulid CatDairyId     = DeterministicUlid(1, 3);
    public static readonly Ulid CatMeatId      = DeterministicUlid(1, 4);
    public static readonly Ulid CatBeverageId  = DeterministicUlid(1, 5);
    public static readonly Ulid CatPantryId    = DeterministicUlid(1, 6);
    public static readonly Ulid CatHouseholdId = DeterministicUlid(1, 7);

    public static readonly Ulid DefaultTaxId   = DeterministicUlid(2, 1);
    public static readonly Ulid DefaultInventoryPolicySettingId = DeterministicUlid(6, 1);

    public static readonly Ulid AdminUserId    = DeterministicUlid(3, 1);
    public static readonly Ulid ManagerUserId  = DeterministicUlid(3, 2);
    public static readonly Ulid StaffUserId    = DeterministicUlid(3, 3);

    public static List<StoreSetting> GetDefaultStoreSettings() => new()
    {
        new StoreSetting
        {
            Id = DefaultInventoryPolicySettingId,
            Key = StoreSetting.InventoryPolicyKey,
            Value = InventorySellPolicy.BlockOnZeroStock.ToString(),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }
    };

    public static List<TaxRate> GetDefaultTaxRates() => new()
    {
        new TaxRate
        {
            Id = DefaultTaxId,
            Name = "VAT",
            Percentage = 0.075m,
            IsInclusive = false,
            IsDefault = true,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }
    };

    private static List<Cashier>? _cachedDefaultCashiers;

    public static List<Cashier> GetDefaultCashiers() => _cachedDefaultCashiers ??= new()
    {
        new Cashier
        {
            Id = AdminUserId,
            Username = "admin",
            Name = "System Administrator",
            PinHash = PasswordHasher.HashPin("1234"),
            Role = UserRole.Admin,
            IsActive = true,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        },
        new Cashier
        {
            Id = ManagerUserId,
            Username = "manager",
            Name = "Floor Manager (Adaeze)",
            PinHash = PasswordHasher.HashPin("2222"),
            Role = UserRole.Manager,
            IsActive = true,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        },
        new Cashier
        {
            Id = StaffUserId,
            Username = "cashier",
            Name = "Cashier (Tunde)",
            PinHash = PasswordHasher.HashPin("0000"),
            Role = UserRole.Staff,
            IsActive = true,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }
    };

    public static List<Category> GetDefaultCategories() => new()
    {
        new Category { Id = CatProduceId,   Name = "Produce",   Description = "Fresh fruits & vegetables", ColorHex = "#22C55E", UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        new Category { Id = CatDairyId,     Name = "Dairy",     Description = "Milk, cheese, eggs & yogurt", ColorHex = "#3B82F6", UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        new Category { Id = CatBakeryId,    Name = "Bakery",    Description = "Freshly baked bread & pastries", ColorHex = "#F59E0B", UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        new Category { Id = CatMeatId,      Name = "Meat",      Description = "Prime cuts, poultry & seafood", ColorHex = "#EF4444", UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        new Category { Id = CatBeverageId,  Name = "Beverage",  Description = "Juices, water, coffee & soft drinks", ColorHex = "#8B5CF6", UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        new Category { Id = CatPantryId,    Name = "Pantry",    Description = "Rice, pasta, oil, grains & spices", ColorHex = "#EC4899", UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        new Category { Id = CatHouseholdId, Name = "Household", Description = "Cleaning & home essentials", ColorHex = "#14B8A6", UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
    };

    public static List<Product> GetDefaultProducts()
    {
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new List<Product>
        {
            new Product { Id = DeterministicUlid(4, 1),  CategoryId = CatProduceId,   Name = "Organic Bananas (Bunch)",           Sku = "PRD-1001",  Barcode = "1001", Unit = "bunch",  CostPrice = 950m,   Price = 1500m,  WholesaleMinQty = 10, WholesaleUnitPrice = 1300m, IsTaxable = false, Stock = 85, ReorderLevel = 15, UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 2),  CategoryId = CatBakeryId,    Name = "Artisan Sourdough Loaf",            Sku = "BAK-1002",  Barcode = "1002", Unit = "loaf",   CostPrice = 2000m,  Price = 3200m,  IsTaxable = true,  Stock = 32, ReorderLevel = 8,  UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 3),  CategoryId = CatDairyId,     Name = "Full Cream Whole Milk 1L",          Sku = "DRY-1003",  Barcode = "1003", Unit = "pack",   CostPrice = 4200m,  Price = 6000m,  IsTaxable = false, Stock = 60, ReorderLevel = 12, UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 4),  CategoryId = CatProduceId,   Name = "Fresh Strawberries (250g)",         Sku = "PRD-1004",  Barcode = "1004", Unit = "punnet", CostPrice = 1900m,  Price = 3000m,  IsTaxable = false, Stock = 24, ReorderLevel = 10, UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 5),  CategoryId = CatMeatId,      Name = "Grass-Fed Beef Ribeye Steak (1kg)", Sku = "MEAT-2001", Barcode = "2001", Unit = "kg",     CostPrice = 9500m,  Price = 14500m, IsTaxable = true,  Stock = 18, ReorderLevel = 5,  UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 6),  CategoryId = CatDairyId,     Name = "Farm Fresh Eggs (Crate 30)",        Sku = "DRY-1006",  Barcode = "1006", Unit = "crate",  CostPrice = 4800m,  Price = 5800m,  IsTaxable = false, Stock = 45, ReorderLevel = 10, UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 7),  CategoryId = CatBeverageId,  Name = "100% Pressed Orange Juice",         Sku = "BEV-1007",  Barcode = "1007", Unit = "bottle", CostPrice = 2100m,  Price = 3200m,  WholesaleMinQty = 6,  WholesaleUnitPrice = 2850m, IsTaxable = true,  Stock = 70, ReorderLevel = 15, UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 8),  CategoryId = CatBeverageId,  Name = "Arabica Roast Coffee 250g",         Sku = "BEV-1008",  Barcode = "1008", Unit = "bag",    CostPrice = 6800m,  Price = 9500m,  IsTaxable = true,  Stock = 28, ReorderLevel = 8,  UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 9),  CategoryId = CatPantryId,    Name = "Royal Basmati Rice (5kg)",          Sku = "GRN-5001",  Barcode = "5001", Unit = "bag",    CostPrice = 11000m, Price = 14500m, IsTaxable = false, Stock = 40, ReorderLevel = 10, UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 10), CategoryId = CatPantryId,    Name = "Pure Vegetable Cooking Oil",        Sku = "PAN-4002",  Barcode = "4002", Unit = "bottle", CostPrice = 3000m,  Price = 4500m,  IsTaxable = true,  Stock = 22, ReorderLevel = 6,  UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 11), CategoryId = CatBakeryId,    Name = "Butter Croissant (Pack of 4)",      Sku = "BAK-1011",  Barcode = "1011", Unit = "pack",   CostPrice = 2800m,  Price = 4500m,  IsTaxable = true,  Stock = 19, ReorderLevel = 8,  UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 12), CategoryId = CatMeatId,      Name = "Free-Range Chicken Breast 1kg",     Sku = "MET-1012",  Barcode = "1012", Unit = "kg",     CostPrice = 6200m,  Price = 8200m,  IsTaxable = false, Stock = 30, ReorderLevel = 8,  UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 13), CategoryId = CatHouseholdId, Name = "Multi-Surface Disinfectant",        Sku = "HSH-1013",  Barcode = "1013", Unit = "bottle", CostPrice = 1800m,  Price = 2800m,  IsTaxable = true,  Stock = 50, ReorderLevel = 10, UpdatedAt = stamp },
            new Product { Id = DeterministicUlid(4, 14), CategoryId = CatDairyId,     Name = "Greek Yogurt Natural 500g",         Sku = "DAI-3004",  Barcode = "3004", Unit = "tub",    CostPrice = 1200m,  Price = 1800m,  IsTaxable = true,  Stock = 8,   ReorderLevel = 10, UpdatedAt = stamp }
        };
    }

    public static List<Customer> GetDefaultCustomers()
    {
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new List<Customer>
        {
            new Customer { Id = DeterministicUlid(5, 1), Name = "Chinedu Okafor",  Phone = "08031234567", Email = "chinedu.okafor@example.com", LoyaltyPoints = 240, StoreCreditBalance = 0m,    TotalSpent = 240000m, VisitCount = 14, CreatedAt = stamp, UpdatedAt = stamp },
            new Customer { Id = DeterministicUlid(5, 2), Name = "Amina Bello",     Phone = "08098765432", Email = "amina.bello@example.com",    LoyaltyPoints = 510, StoreCreditBalance = 5000m, TotalSpent = 510000m, VisitCount = 29, CreatedAt = stamp, UpdatedAt = stamp },
            new Customer { Id = DeterministicUlid(5, 3), Name = "Olumide Adeyemi", Phone = "07055551212", Email = "olumide.a@example.com",      LoyaltyPoints = 85,  StoreCreditBalance = 0m,    TotalSpent = 85000m,  VisitCount = 5,  CreatedAt = stamp, UpdatedAt = stamp }
        };
    }
}
