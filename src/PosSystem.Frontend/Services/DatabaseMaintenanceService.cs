using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PosSystem.Frontend.Data;
using PosSystem.Shared.Models;

namespace PosSystem.Frontend.Services;

public record BackupResult(bool Success, string BackupFilePath, long FileSizeBytes, string Message);
public record CsvExportResult(bool Success, string ExportDirectory, int FilesExported, int TotalRowsExported, string Message);
public record CsvImportResult(bool Success, string ImportFilePath, int CreatedCount, int UpdatedCount, string Message);

public class DatabaseMaintenanceService
{
    private readonly IServiceProvider _serviceProvider;

    public DatabaseMaintenanceService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public static string GetBaseDataDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PosSystem");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetBackupsDirectory()
    {
        var dir = Path.Combine(GetBaseDataDirectory(), "Backups");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetExportsDirectory()
    {
        var dir = Path.Combine(GetBaseDataDirectory(), "Exports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetImportsDirectory()
    {
        var dir = Path.Combine(GetBaseDataDirectory(), "Imports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public BackupResult CreateHotBackup(string? targetDirectory = null, string? reasonTag = null)
    {
        string backupDir = string.IsNullOrWhiteSpace(targetDirectory)
            ? GetBackupsDirectory()
            : targetDirectory!;
        Directory.CreateDirectory(backupDir);

        string safeTag = string.IsNullOrWhiteSpace(reasonTag)
            ? "manual"
            : new string(reasonTag!.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrEmpty(safeTag)) safeTag = "backup";

        string fileName = $"pos_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{safeTag}.db";
        string fullBackupPath = Path.Combine(backupDir, fileName);

        if (File.Exists(fullBackupPath))
            File.Delete(fullBackupPath);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        string escapedPath = fullBackupPath.Replace("'", "''");
        string vacuumSql = string.Concat("VACUUM INTO '", escapedPath, "';");
        db.Database.ExecuteSqlRaw(vacuumSql);

        PruneOldBackups(backupDir, maxRetained: 30);

        var info = new FileInfo(fullBackupPath);
        long size = info.Exists ? info.Length : 0L;
        return new BackupResult(
            true,
            fullBackupPath,
            size,
            $"💾 Created hot SQLite backup '{fileName}' ({size / 1024.0:0.0} KB) in {backupDir}.");
    }

    private static void PruneOldBackups(string backupDir, int maxRetained)
    {
        try
        {
            var files = new DirectoryInfo(backupDir)
                .GetFiles("pos_backup_*.db")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(maxRetained)
                .ToList();

            foreach (var oldFile in files)
            {
                oldFile.Delete();
            }
        }
        catch
        {
            // Best-effort pruning
        }
    }

    public CsvExportResult ExportAllReportsToCsv(string? targetDirectory = null)
    {
        string exportDir = string.IsNullOrWhiteSpace(targetDirectory)
            ? GetExportsDirectory()
            : targetDirectory!;
        Directory.CreateDirectory(exportDir);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        int totalRows = 0;
        var categoriesById = db.Categories.AsNoTracking().ToDictionary(c => c.Id, c => c.Name);

        // 1. Products Catalog CSV
        var products = db.Products.AsNoTracking().OrderBy(p => p.Sku).ToList();
        var prodSb = new StringBuilder();
        prodSb.AppendLine("Sku,Name,Barcode,CategoryName,Unit,CostPrice,Price,WholesaleMinQty,WholesaleUnitPrice,Stock,ReorderLevel,IsTaxable,ExpiryDate");
        foreach (var p in products)
        {
            string catName = categoriesById.TryGetValue(p.CategoryId, out var cn) ? cn : "General";
            string exp = p.ExpiryDate.HasValue ? p.ExpiryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
            prodSb.AppendLine(string.Join(",",
                EscapeCsv(p.Sku),
                EscapeCsv(p.Name),
                EscapeCsv(p.Barcode ?? ""),
                EscapeCsv(catName),
                EscapeCsv(p.Unit),
                p.CostPrice.ToString("0.00", CultureInfo.InvariantCulture),
                p.Price.ToString("0.00", CultureInfo.InvariantCulture),
                p.WholesaleMinQty.ToString(CultureInfo.InvariantCulture),
                p.WholesaleUnitPrice.ToString("0.00", CultureInfo.InvariantCulture),
                p.Stock.ToString(CultureInfo.InvariantCulture),
                p.ReorderLevel.ToString(CultureInfo.InvariantCulture),
                p.IsTaxable ? "true" : "false",
                EscapeCsv(exp)));
            totalRows++;
        }
        File.WriteAllText(Path.Combine(exportDir, "products_catalog.csv"), prodSb.ToString(), Encoding.UTF8);

        // 2. Sales & Refunds History CSV
        var sales = db.Sales.AsNoTracking().OrderByDescending(s => s.CreatedAt).Take(1000).ToList();
        var salesSb = new StringBuilder();
        salesSb.AppendLine("ReceiptNumber,CreatedAtUtc,Status,CashierName,CustomerName,PaymentMethod,Subtotal,DiscountAmount,LoyaltyPointsRedeemed,TaxAmount,TotalAmount,RefundedAmount,CashTendered,CardTendered,TransferTendered,StoreCreditTendered,ChangeDue");
        foreach (var s in sales)
        {
            salesSb.AppendLine(string.Join(",",
                EscapeCsv(s.ReceiptNumber),
                s.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                s.Status,
                EscapeCsv(s.CashierName),
                EscapeCsv(s.CustomerName ?? "Walk-in"),
                s.PaymentMethod,
                s.Subtotal.ToString("0.00", CultureInfo.InvariantCulture),
                s.DiscountAmount.ToString("0.00", CultureInfo.InvariantCulture),
                s.LoyaltyPointsRedeemed.ToString(CultureInfo.InvariantCulture),
                s.TaxAmount.ToString("0.00", CultureInfo.InvariantCulture),
                s.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture),
                s.RefundedAmount.ToString("0.00", CultureInfo.InvariantCulture),
                s.CashTendered.ToString("0.00", CultureInfo.InvariantCulture),
                s.CardTendered.ToString("0.00", CultureInfo.InvariantCulture),
                s.TransferTendered.ToString("0.00", CultureInfo.InvariantCulture),
                s.StoreCreditTendered.ToString("0.00", CultureInfo.InvariantCulture),
                s.ChangeDue.ToString("0.00", CultureInfo.InvariantCulture)));
            totalRows++;
        }
        File.WriteAllText(Path.Combine(exportDir, "sales_history.csv"), salesSb.ToString(), Encoding.UTF8);

        // 3. Inventory Movements CSV
        var movements = db.InventoryMovements.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(1000).ToList();
        var movSb = new StringBuilder();
        movSb.AppendLine("CreatedAtUtc,ProductName,MovementType,QuantityDelta,StockAfter,ReferenceId,PerformedBy,Reason");
        foreach (var m in movements)
        {
            movSb.AppendLine(string.Join(",",
                m.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                EscapeCsv(m.ProductName),
                m.Type,
                m.QuantityDelta.ToString(CultureInfo.InvariantCulture),
                m.StockAfter.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(m.ReferenceId ?? ""),
                EscapeCsv(m.PerformedBy),
                EscapeCsv(m.Reason ?? "")));
            totalRows++;
        }
        File.WriteAllText(Path.Combine(exportDir, "inventory_movements.csv"), movSb.ToString(), Encoding.UTF8);

        // 4. Security Audit Trail CSV
        var audits = db.AuditLogs.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(1000).ToList();
        var audSb = new StringBuilder();
        audSb.AppendLine("CreatedAtUtc,TerminalId,ActionType,Description,PerformedByName,AuthorizedByName,ReferenceId");
        foreach (var a in audits)
        {
            audSb.AppendLine(string.Join(",",
                a.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                EscapeCsv(a.TerminalId),
                EscapeCsv(a.ActionType),
                EscapeCsv(a.Description),
                EscapeCsv(a.PerformedByName),
                EscapeCsv(a.AuthorizedByName),
                EscapeCsv(a.ReferenceId ?? "")));
            totalRows++;
        }
        File.WriteAllText(Path.Combine(exportDir, "security_audit_logs.csv"), audSb.ToString(), Encoding.UTF8);

        return new CsvExportResult(
            true,
            exportDir,
            4,
            totalRows,
            $"📊 Exported 4 CSV reports ({totalRows} rows) to {exportDir}.");
    }

    public CsvImportResult ImportProductsFromCsv(string? csvFilePath = null)
    {
        string importPath = string.IsNullOrWhiteSpace(csvFilePath)
            ? Path.Combine(GetImportsDirectory(), "products_import.csv")
            : csvFilePath!;

        if (!File.Exists(importPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(importPath)!);
            var template = new StringBuilder();
            template.AppendLine("Sku,Name,Barcode,CategoryName,Unit,CostPrice,Price,WholesaleMinQty,WholesaleUnitPrice,Stock,ReorderLevel,IsTaxable,ExpiryDate");
            template.AppendLine("BEV-2090,Sparkling Mineral Water (Pack of 6),2090,Beverage,pack,2400.00,3500.00,4,3100.00,40,10,true,");
            template.AppendLine("BAK-2091,Fresh Butter Brioche Loaf,2091,Bakery,loaf,1800.00,2900.00,0,0.00,25,6,true,2026-12-31");
            File.WriteAllText(importPath, template.ToString(), Encoding.UTF8);
        }

        var lines = File.ReadAllLines(importPath, Encoding.UTF8)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count <= 1)
        {
            return new CsvImportResult(false, importPath, 0, 0, $"No product rows found in {importPath}.");
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var categories = db.Categories.ToList();
        var defaultCategory = categories.FirstOrDefault() ?? new Category { Id = Ulid.NewUlid(), Name = "General", IsActive = true };
        if (!categories.Any())
        {
            db.Categories.Add(defaultCategory);
            categories.Add(defaultCategory);
        }

        int created = 0;
        int updated = 0;

        foreach (var line in lines.Skip(1))
        {
            var cols = ParseCsvLine(line);
            if (cols.Count < 7) continue;

            string sku = cols[0].Trim();
            string name = cols[1].Trim();
            if (string.IsNullOrEmpty(sku) || string.IsNullOrEmpty(name)) continue;

            string barcode = string.IsNullOrWhiteSpace(cols[2]) ? sku : cols[2].Trim();
            string catName = string.IsNullOrWhiteSpace(cols[3]) ? "General" : cols[3].Trim();
            string unit = string.IsNullOrWhiteSpace(cols[4]) ? "ea" : cols[4].Trim();

            decimal.TryParse(cols[5], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal costPrice);
            decimal.TryParse(cols[6], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price);
            if (price <= 0m) continue;

            int wholesaleMin = cols.Count > 7 && int.TryParse(cols[7], NumberStyles.Any, CultureInfo.InvariantCulture, out int wm) ? wm : 0;
            decimal wholesalePrice = cols.Count > 8 && decimal.TryParse(cols[8], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal wp) ? wp : 0m;
            int stock = cols.Count > 9 && int.TryParse(cols[9], NumberStyles.Any, CultureInfo.InvariantCulture, out int st) ? st : 0;
            int reorder = cols.Count > 10 && int.TryParse(cols[10], NumberStyles.Any, CultureInfo.InvariantCulture, out int ro) ? ro : 10;
            bool taxable = cols.Count <= 11 || !bool.TryParse(cols[11], out bool tx) || tx;

            DateTime? expiry = null;
            if (cols.Count > 12 && DateTime.TryParse(cols[12], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsedExp))
            {
                expiry = parsedExp.Date;
            }

            var cat = categories.FirstOrDefault(c => string.Equals(c.Name, catName, StringComparison.OrdinalIgnoreCase));
            if (cat == null)
            {
                cat = new Category
                {
                    Id = Ulid.NewUlid(),
                    Name = catName,
                    Description = $"{catName} department",
                    ColorHex = "#38bdf8",
                    IsActive = true,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Categories.Add(cat);
                categories.Add(cat);
            }

            var existing = db.Products.FirstOrDefault(p => p.Sku.ToLower() == sku.ToLower());
            if (existing == null)
            {
                db.Products.Add(new Product
                {
                    Id = Ulid.NewUlid(),
                    CategoryId = cat.Id,
                    Sku = sku,
                    Name = name,
                    Barcode = barcode,
                    Unit = unit,
                    CostPrice = costPrice,
                    Price = price,
                    WholesaleMinQty = wholesaleMin,
                    WholesaleUnitPrice = wholesalePrice,
                    Stock = stock,
                    ReorderLevel = reorder,
                    IsTaxable = taxable,
                    ExpiryDate = expiry,
                    IsActive = true,
                    UpdatedAt = DateTime.UtcNow
                });
                created++;
            }
            else
            {
                existing.CategoryId = cat.Id;
                existing.Name = name;
                existing.Barcode = barcode;
                existing.Unit = unit;
                existing.CostPrice = costPrice;
                existing.Price = price;
                existing.WholesaleMinQty = wholesaleMin;
                existing.WholesaleUnitPrice = wholesalePrice;
                existing.Stock = stock;
                existing.ReorderLevel = reorder;
                existing.IsTaxable = taxable;
                existing.ExpiryDate = expiry;
                existing.IsActive = true;
                existing.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
        }

        db.SaveChanges();
        return new CsvImportResult(
            true,
            importPath,
            created,
            updated,
            $"📥 Imported CSV catalog from {Path.GetFileName(importPath)}: {created} created, {updated} updated.");
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }
        result.Add(current.ToString());
        return result;
    }
}
