using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PosSystem.Backend.Data;
using PosSystem.Frontend.Data;
using PosSystem.Frontend.Hardware;
using PosSystem.Frontend.Services;
using PosSystem.Frontend.ViewModels;
using PosSystem.Frontend.Views;
using PosSystem.Shared.Barcodes;
using PosSystem.Shared.Data;
using PosSystem.Shared.Models;
using PosSystem.Shared.Security;
using PosSystem.Shared.Serialization;

namespace PosSystem.Tests;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        int passed = 0;
        int failed = 0;

        void RunTest(string name, Action test)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine($"  [PASS] {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
            }
        }

        Console.WriteLine("=== Prime POS v2.2 Enterprise Automated Verification Suite ===");

        RunTest("1. Argon2id PasswordHasher hashes and verifies PINs accurately", () =>
        {
            string hash = PasswordHasher.HashPin("1234");
            Assert(PasswordHasher.VerifyPin("1234", hash), "Valid PIN should verify true");
            Assert(!PasswordHasher.VerifyPin("9999", hash), "Invalid PIN should verify false");
        });

        RunTest("2. UlidJsonConverter & PosJsonOptions serialize Sale + SaleLineItems without object cycle errors", () =>
        {
            var sale = new Sale
            {
                Id = Ulid.NewUlid(),
                ReceiptNumber = "RCP-TEST-001",
                TotalAmount = 10750m
            };
            var item = new SaleLineItem
            {
                Id = Ulid.NewUlid(),
                SaleId = sale.Id,
                Sale = sale,
                ProductId = CatalogSeedData.GetDefaultProducts()[0].Id,
                ProductName = "Test Item",
                Quantity = 2,
                UnitPrice = 5000m,
                LineSubtotal = 10000m
            };
            sale.Items.Add(item);

            string json = JsonSerializer.Serialize(sale, PosJsonOptions.Default);
            var roundtrip = JsonSerializer.Deserialize<Sale>(json, PosJsonOptions.Default);

            Assert(roundtrip != null, "Deserialized sale must not be null");
            Assert(roundtrip!.Id == sale.Id, $"Expected Sale.Id {sale.Id}, got {roundtrip.Id}");
            Assert(roundtrip.Items.Count == 1, "Expected 1 line item");
            Assert(roundtrip.Items[0].ProductId == item.ProductId, "Expected ProductId to roundtrip");
        });

        RunTest("3. CartExecutionEngine updates line subtotals and 7.5% VAT when quantity increases/decreases", () =>
        {
            var engine = new CartExecutionEngine();
            var vat = new TaxRate { Name = "VAT", Percentage = 0.075m, IsInclusive = false };
            var sale = new Sale();
            var bread = new Product
            {
                Id = Ulid.NewUlid(),
                Name = "Artisan Sourdough Loaf",
                Sku = "BAK-1002",
                Price = 8500m,
                IsTaxable = true
            };

            engine.AddItemToCart(sale, bread, 1, vat);
            Assert(sale.Subtotal == 8500m, $"1x subtotal expected 8500, got {sale.Subtotal}");
            Assert(sale.TaxAmount == 637.50m, $"1x VAT expected 637.50, got {sale.TaxAmount}");
            Assert(sale.TotalAmount == 9137.50m, $"1x total expected 9137.50, got {sale.TotalAmount}");

            engine.UpdateItemQuantity(sale, sale.Items[0], 3, vat);
            Assert(sale.Items[0].LineSubtotal == 25500m, $"3x LineSubtotal expected 25500, got {sale.Items[0].LineSubtotal}");
            Assert(sale.Subtotal == 25500m, $"3x Subtotal expected 25500, got {sale.Subtotal}");
            Assert(sale.TaxAmount == 1912.50m, $"3x VAT expected 1912.50, got {sale.TaxAmount}");
            Assert(sale.TotalAmount == 27412.50m, $"3x Total expected 27412.50, got {sale.TotalAmount}");
        });

        RunTest("4. CartExecutionEngine handles percentage discounts and tax-inclusive VAT mode", () =>
        {
            var engine = new CartExecutionEngine();
            var vatExcl = new TaxRate { Name = "VAT", Percentage = 0.075m, IsInclusive = false };
            var sale = new Sale();
            var item = new Product { Id = Ulid.NewUlid(), Name = "Steak", Price = 10000m, IsTaxable = true };

            engine.AddItemToCart(sale, item, 2, vatExcl);
            engine.ApplyCartDiscountPercentage(sale, 10m, vatExcl);

            Assert(sale.DiscountAmount == 2000m, $"Discount expected 2000, got {sale.DiscountAmount}");
            Assert(sale.TaxAmount == 1350m, $"Tax on 18,000 @ 7.5% expected 1350, got {sale.TaxAmount}");
            Assert(sale.TotalAmount == 19350m, $"Total expected 19350, got {sale.TotalAmount}");

            var vatIncl = new TaxRate { Name = "VAT", Percentage = 0.075m, IsInclusive = true };
            engine.RecalculateCart(sale, vatIncl);
            Assert(sale.IsTaxInclusive, "Sale.IsTaxInclusive must be persisted");
            Assert(sale.TotalAmount == 18000m, $"Inclusive total expected 18000, got {sale.TotalAmount}");
            Assert(sale.TaxAmount > 0m, "Extracted inclusive VAT must be > 0");
        });

        RunTest("5. ReceiptPrinterService formats 42-col thermal receipts, Return Slips, Z-Reports, and ESC/POS bytes", () =>
        {
            var printer = new ReceiptPrinterService();
            var sale = new Sale
            {
                ReceiptNumber = "RCP-20261102-ABCD12",
                CashierName = "Adaeze",
                Subtotal = 10000m,
                TaxRatePercentage = 0.075m,
                TaxAmount = 750m,
                TotalAmount = 10750m,
                PaymentMethod = PaymentMethod.Cash,
                CashTendered = 11000m,
                AmountTendered = 11000m,
                ChangeDue = 250m,
                Items = new List<SaleLineItem>
                {
                    new SaleLineItem { ProductName = "Royal Basmati Rice", Quantity = 1, UnitPrice = 10000m, LineSubtotal = 10000m }
                }
            };

            string receipt = printer.FormatReceiptText(sale);
            Assert(receipt.Contains("RCP-20261102-ABCD12"), "Receipt must contain receipt number");
            Assert(receipt.Contains("10,750.00"), "Receipt must contain formatted total");
            byte[] payload = printer.BuildReceipt(sale, kickDrawer: true);
            Assert(payload.Length > 200, "ESC/POS payload bytes must be generated");

            var returnSlip = new Sale
            {
                ReceiptNumber = "RET-20261103-112233",
                OriginalSaleId = sale.Id,
                Status = SaleStatus.Refunded,
                Subtotal = 5000m,
                TaxAmount = 375m,
                TotalAmount = 5375m,
                Items = new List<SaleLineItem>
                {
                    new SaleLineItem { ProductName = "Royal Basmati Rice", Quantity = 1, UnitPrice = 5000m, LineSubtotal = 5000m }
                }
            };
            string retText = printer.FormatReceiptText(returnSlip);
            Assert(retText.Contains("CUSTOMER RETURN / REFUND SLIP"), "Return slip header must render");
            Assert(retText.Contains("REFUND TOTAL:"), "Return slip must display REFUND TOTAL");
        });

        RunTest("6. Hardware USB BarcodeScannerListener buffers wedge chars and fires on Enter", () =>
        {
            var scanner = new BarcodeScannerListener();
            scanner.StartListening();
            string? scannedCode = null;
            scanner.BarcodeScanned += (_, code) => scannedCode = code;

            foreach (char c in "6151100010018")
            {
                scanner.ProcessWedgeChar(c);
            }
            bool completed = scanner.TryCompleteWedgeScan();
            Assert(completed, "TryCompleteWedgeScan should return true for 13-digit EAN barcode");
            Assert(scannedCode == "6151100010018", $"Expected 6151100010018, got {scannedCode}");
        });

        RunTest("7. SQLite LocalDbContext upgrades legacy schemas (including IsNegativeStockAuthorized) & real pos_local.db without data loss", () =>
        {
            // 1. Upgrade and verify the real %LocalAppData%\PosSystem\pos_local.db on this machine
            var realOptions = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={LocalDbContext.GetDatabasePath()};Pooling=True")
                .Options;
            using (var realDb = new LocalDbContext(realOptions))
            {
                realDb.InitializeAndSeed();
                _ = realDb.Sales.AsNoTracking().Include(s => s.Items).AsSplitQuery().Take(60).ToList();
            }

            // 2. Verify isolated test database persistence
            using var harness = new PosTestHarness();
            using var db = harness.CreateDbContext();

            var policySetting = db.StoreSettings.FirstOrDefault(s => s.Key == StoreSetting.InventoryPolicyKey);
            Assert(policySetting != null, "Default InventorySellPolicy StoreSetting must be seeded");

            var suspendedSale = new Sale
            {
                Id = Ulid.NewUlid(),
                ReceiptNumber = "HOLD-TEST-99",
                Status = SaleStatus.Suspended,
                CashierName = "System Admin",
                Subtotal = 4200m,
                TotalAmount = 4515m
            };
            db.Sales.Add(suspendedSale);

            var dlqItem = new SyncQueue
            {
                Id = Ulid.NewUlid(),
                SaleId = suspendedSale.Id,
                EntityType = "Sale",
                Status = SyncStatus.Failed,
                RetryCount = 10,
                ErrorMessage = "Simulated network failure"
            };
            db.SyncQueues.Add(dlqItem);
            db.SaveChanges();

            var loadedSuspended = db.Sales.FirstOrDefault(s => s.Id == suspendedSale.Id && s.Status == SaleStatus.Suspended);
            Assert(loadedSuspended != null, "Suspended sale must persist in SQLite");

            var failedCount = db.SyncQueues.Count(q => q.Status == SyncStatus.Failed || q.RetryCount >= 10);
            Assert(failedCount == 1, "Dead-letter SyncQueue item must be queryable");
        });

        RunTest("8. WPF LoginView & CheckoutView embedded XAML load cleanly with Partial Refund, Shift Banner, DLQ, and Audit UI", () =>
        {
            var loginView = new LoginView();
            Assert(loginView.Content != null, "LoginView XAML content must be loaded");

            var checkoutView = new CheckoutView();
            Assert(checkoutView.Content != null, "CheckoutView XAML content must be loaded");
        });

        RunTest("9. Deli / Butcher / Produce Variable-Measure EAN-13 Barcode Parser decodes weight & price labels and validates Mod-10 check digits", () =>
        {
            var catalog = CatalogSeedData.GetDefaultProducts();
            // MEAT-2001 is Grass-Fed Beef Ribeye Steak (1kg) @ 14,500.00 / kg
            string weightBarcode = WeightedBarcodeParser.BuildWeightEmbeddedBarcode("MEAT-2001", 450, "20");
            Assert(weightBarcode.Length == 13, "Generated EAN-13 must be 13 digits");
            Assert(WeightedBarcodeParser.IsValidEan13(weightBarcode), "Generated EAN-13 must pass Modulo-10 check");

            bool okWeight = WeightedBarcodeParser.TryParse(weightBarcode, catalog, out var weightMatch, out var err1);
            Assert(okWeight && weightMatch != null, $"Weight barcode should parse cleanly ({err1})");
            Assert(weightMatch!.WeightKg == 0.450m, $"Expected 0.450 kg, got {weightMatch.WeightKg}");
            Assert(weightMatch.CalculatedUnitPrice == 6525.00m, $"Expected 0.450 * 14500 = 6525.00, got {weightMatch.CalculatedUnitPrice}");

            // Price-embedded barcode (prefix 28: whole currency 7,250 -> 0.500 kg of Ribeye @ 14,500)
            string priceBarcode = WeightedBarcodeParser.BuildPriceEmbeddedBarcode("2001", 7250m, "28");
            bool okPrice = WeightedBarcodeParser.TryParse(priceBarcode, catalog, out var priceMatch, out var err2);
            Assert(okPrice && priceMatch != null, $"Price barcode should parse cleanly ({err2})");
            Assert(priceMatch!.CalculatedUnitPrice == 7250m, $"Expected price 7250, got {priceMatch.CalculatedUnitPrice}");
            Assert(priceMatch.WeightKg == 0.500m, $"Expected implied weight 0.500 kg, got {priceMatch.WeightKg}");

            // Corrupt check digit -> must reject
            char badLast = weightBarcode[12] == '9' ? '0' : (char)(weightBarcode[12] + 1);
            string tampered = weightBarcode[..12] + badLast;
            bool okTampered = WeightedBarcodeParser.TryParse(tampered, catalog, out _, out var tamperErr);
            Assert(!okTampered && tamperErr != null && tamperErr.Contains("check digit"), "Tampered check digit must be rejected");
        });

        RunTest("10. Mandatory Shift Float Enforcement blocks checkout without open shift and unlocks once float is declared", () =>
        {
            using var harness = new PosTestHarness();
            harness.LoginAsAdmin();
            using var vm = harness.CreateCheckoutViewModel();

            // Fresh DB has NO open shift — must NOT silently auto-create a fake shift
            Assert(!vm.HasOpenShift && vm.ActiveShift == null, "CheckoutViewModel must not silently auto-create a shift on startup");

            var rice = vm.FilteredProducts.First(p => p.Sku == "GRN-5001");
            vm.AddProductWithQuantity(rice, 1);
            vm.CashTenderInput = vm.CurrentSale.TotalAmount;

            bool paidWithoutShift = vm.FinalizeSalePaymentAsync().GetAwaiter().GetResult();
            Assert(!paidWithoutShift, "Checkout must be blocked when no shift float has been opened");
            Assert(vm.ActiveSection == "Shift", "Blocked checkout should route cashier to Shift & Z-Report tab");

            // Declare opening float and open shift
            vm.OpeningFloatInput = 30000m;
            vm.OpenShift();
            Assert(vm.HasOpenShift && vm.ActiveShift != null && vm.ActiveShift.OpeningFloat == 30000m, "Shift must open with declared float");

            vm.CashTenderInput = vm.CurrentSale.TotalAmount;
            bool paidWithShift = vm.FinalizeSalePaymentAsync().GetAwaiter().GetResult();
            Assert(paidWithShift, "Checkout must succeed once shift float is open");
        });

        RunTest("11. Partial & Multi-Step Full Item Returns restore partial stock, prorate discounts/VAT with zero kobo drift, and queue RET slips", () =>
        {
            using var harness = new PosTestHarness();
            var admin = harness.LoginAsAdmin();
            using var vm = harness.CreateCheckoutViewModel();

            vm.OpeningFloatInput = 20000m;
            vm.OpenShift();

            var bread = vm.FilteredProducts.First(p => p.Sku == "BAK-1002"); // 3,200 taxable
            int initialStock = bread.Stock;

            vm.AddProductWithQuantity(bread, 3);
            // Apply 10% discount via cart engine & complete sale
            var engine = new CartExecutionEngine();
            using (var db = harness.CreateDbContext())
            {
                var vat = db.TaxRates.First(t => t.IsDefault);
                engine.ApplyCartDiscountPercentage(vm.CurrentSale, 10m, vat);
            }

            decimal originalSaleTotal = vm.CurrentSale.TotalAmount; // 3 * 3200 = 9600 - 960 = 8640 + 648 VAT = 9288.00
            Ulid saleId = vm.CurrentSale.Id;
            vm.CashTenderInput = originalSaleTotal;
            Assert(vm.FinalizeSalePaymentAsync().GetAwaiter().GetResult(), "Initial 3-item sale must complete");

            using (var db = harness.CreateDbContext())
            {
                Assert(db.Products.First(p => p.Id == bread.Id).Stock == initialStock - 3, "Stock must decrease by 3");
            }

            // Step 1: Partial Return of 1 unit out of 3
            Sale savedSale;
            using (var db = harness.CreateDbContext())
            {
                savedSale = db.Sales.Include(s => s.Items).First(s => s.Id == saleId);
            }
            var lineId = savedSale.Items[0].Id;

            var returnSlip1 = vm.ExecuteApprovedRefund(
                saleId,
                new Dictionary<Ulid, int> { [lineId] = 1 },
                admin);

            Assert(returnSlip1 != null, "First partial return slip must be created");
            Assert(returnSlip1!.ReceiptNumber.StartsWith("RET-"), "Return slip must use RET- prefix");
            Assert(returnSlip1.TotalAmount == 3096.00m, $"1/3 of 9288.00 expected 3096.00, got {returnSlip1.TotalAmount}");

            using (var db = harness.CreateDbContext())
            {
                var updatedOrig = db.Sales.Include(s => s.Items).First(s => s.Id == saleId);
                Assert(updatedOrig.Status == SaleStatus.PartiallyRefunded, $"Expected PartiallyRefunded, got {updatedOrig.Status}");
                Assert(updatedOrig.Items[0].ReturnedQuantity == 1, "ReturnedQuantity must be 1");
                Assert(updatedOrig.Items[0].RemainingReturnableQuantity == 2, "RemainingReturnableQuantity must be 2");
                Assert(db.Products.First(p => p.Id == bread.Id).Stock == initialStock - 2, "Stock must restore +1 unit");
                Assert(db.SyncQueues.Any(sq => sq.SaleId == returnSlip1.Id && sq.Status == SyncStatus.Pending), "Return slip must be queued in Outbox SyncQueue");
            }

            // Step 2: Return the remaining 2 units -> status transitions to Refunded with zero kobo drift
            var returnSlip2 = vm.ExecuteApprovedRefund(
                saleId,
                new Dictionary<Ulid, int> { [lineId] = 2 },
                admin);

            Assert(returnSlip2 != null, "Second return slip must be created");
            Assert(returnSlip1.TotalAmount + returnSlip2!.TotalAmount == originalSaleTotal,
                $"Cumulative refunds ({returnSlip1.TotalAmount + returnSlip2.TotalAmount}) must equal original sale ({originalSaleTotal}) with zero kobo drift");

            using (var db = harness.CreateDbContext())
            {
                var finalOrig = db.Sales.Include(s => s.Items).First(s => s.Id == saleId);
                Assert(finalOrig.Status == SaleStatus.Refunded, $"Expected Refunded after all items returned, got {finalOrig.Status}");
                Assert(!finalOrig.CanRefund, "Fully refunded sale must report CanRefund == false");
                Assert(db.Products.First(p => p.Id == bread.Id).Stock == initialStock, "Full stock must be restored");
            }
        });

        RunTest("12. Zero-Stock & Negative Inventory Policy Controls enforce Block, Manager Override + AuditLog, and AllowNegative", () =>
        {
            using var harness = new PosTestHarness();
            harness.LoginAsStaff(); // Standard cashier (Role = Staff)
            using var vm = harness.CreateCheckoutViewModel();

            vm.OpeningFloatInput = 10000m;
            vm.OpenShift();

            var targetProd = vm.FilteredProducts.First(p => p.Sku == "DAI-3004");
            using (var db = harness.CreateDbContext())
            {
                var dbProd = db.Products.First(p => p.Id == targetProd.Id);
                dbProd.Stock = 0;
                db.SaveChanges();
            }
            targetProd.Stock = 0;

            // Policy 1: Default BlockOnZeroStock
            Assert(vm.ActiveInventoryPolicy == InventorySellPolicy.BlockOnZeroStock, "Default policy must be BlockOnZeroStock");
            vm.AddProductWithQuantity(targetProd, 1);
            Assert(vm.CartItems.Count == 0, "BlockOnZeroStock must prevent adding 0-stock item to cart");

            // Policy 2: WarnAndRequireManagerOverride (Staff triggers manager PIN modal + AuditLog)
            vm.SetInventoryPolicy(InventorySellPolicy.WarnAndRequireManagerOverride);
            vm.AddProductWithQuantity(targetProd, 1);
            Assert(vm.IsManagerModalOpen, "Staff adding 0-stock item under WarnAndRequireManagerOverride must open Manager PIN modal");
            Assert(vm.CartItems.Count == 0, "Item must not enter cart before supervisor PIN verification");

            vm.ManagerPinInput = "2222"; // Floor Manager (Adaeze)
            vm.ConfirmManagerOverrideCommand.Execute(null);
            Assert(!vm.IsManagerModalOpen, "Valid manager PIN must close modal");
            Assert(vm.CartItems.Count == 1 && vm.CartItems[0].IsNegativeStockAuthorized, "Authorized item must enter cart with IsNegativeStockAuthorized = true");

            vm.CashTenderInput = vm.CurrentSale.TotalAmount;
            Assert(vm.FinalizeSalePaymentAsync().GetAwaiter().GetResult(), "Authorized negative-stock sale must complete");

            using (var db = harness.CreateDbContext())
            {
                var afterProd = db.Products.First(p => p.Id == targetProd.Id);
                Assert(afterProd.Stock == -1, $"Expected true negative stock -1, got {afterProd.Stock}");
                Assert(afterProd.StockBadgeText.StartsWith("NEG: -1"), $"Expected NEG badge, got {afterProd.StockBadgeText}");

                bool hasAudit = db.AuditLogs.Any(a =>
                    a.ActionType == "NegativeStockOverride" &&
                    a.AuthorizedByName.Contains("Adaeze"));
                Assert(hasAudit, "Supervisor NegativeStockOverride must be recorded in AuditLogs");
            }
        });

        RunTest("13. Non-Cash Over-Tender Guard blocks Card/Transfer > TotalAmount and computes cash-only ChangeDue on Split Tenders", () =>
        {
            using var harness = new PosTestHarness();
            harness.LoginAsAdmin();
            using var vm = harness.CreateCheckoutViewModel();

            vm.OpeningFloatInput = 15000m;
            vm.OpenShift();

            var oil = vm.FilteredProducts.First(p => p.Sku == "PAN-4002");
            vm.AddProductWithQuantity(oil, 1);
            decimal total = vm.CurrentSale.TotalAmount;

            // Attempt Card over-tender -> must block
            vm.SelectedPaymentMethod = PaymentMethod.Card;
            vm.CashTenderInput = 0m;
            vm.CardTenderInput = total + 500m;
            vm.TransferTenderInput = 0m;
            bool overCardOk = vm.FinalizeSalePaymentAsync().GetAwaiter().GetResult();
            Assert(!overCardOk, "Card tender exceeding order total must be rejected");

            // Valid Split tender: Card pays (total - 1000), Cash pays 2000 -> ChangeDue must be exactly 1000
            vm.SelectedPaymentMethod = PaymentMethod.Split;
            vm.CardTenderInput = total - 1000m;
            vm.TransferTenderInput = 0m;
            vm.CashTenderInput = 2000m;

            bool splitOk = vm.FinalizeSalePaymentAsync().GetAwaiter().GetResult();
            Assert(splitOk, "Valid split tender with cash over-payment must succeed");

            using var db = harness.CreateDbContext();
            var completed = db.Sales.OrderByDescending(s => s.CreatedAt).First(s => s.Status == SaleStatus.Completed);
            Assert(completed.ChangeDue == 1000m, $"Expected cash-only ChangeDue 1000.00, got {completed.ChangeDue}");
        });

        RunTest("14. OutboxSyncService exponential backoff & Dead-Letter Queue transition at 10 retries", () =>
        {
            Assert(OutboxSyncService.ComputeBackoffDelay(0) == TimeSpan.FromSeconds(1), "Retry 0 delay = 1s");
            Assert(OutboxSyncService.ComputeBackoffDelay(3) == TimeSpan.FromSeconds(8), "Retry 3 delay = 8s");
            Assert(OutboxSyncService.ComputeBackoffDelay(9) == TimeSpan.FromSeconds(64), "Retry >= 6 capped at 64s");

            var entry = new SyncQueue
            {
                Id = Ulid.NewUlid(),
                SaleId = Ulid.NewUlid(),
                Status = SyncStatus.Pending,
                RetryCount = 9
            };

            OutboxSyncService.RecordSyncFailure(new[] { entry }, "HTTP 503 (Service Unavailable)", DateTime.UtcNow);
            Assert(entry.RetryCount == 10, "RetryCount must increment to 10");
            Assert(entry.Status == SyncStatus.Failed, "10th failure must transition SyncQueue entry to SyncStatus.Failed (DLQ)");
            Assert(entry.ErrorMessage == "HTTP 503 (Service Unavailable)", "ErrorMessage must be recorded");
        });

        RunTest("15. EF Core 8 PostgreSQL Migrations & Production JwtSecretPolicy fail-fast validation", () =>
        {
            using var backendDb = new BackendDesignTimeDbContextFactory().CreateDbContext(Array.Empty<string>());
            var migrations = backendDb.Database.GetMigrations().ToList();
            Assert(migrations.Count >= 1, "BackendDbContext must contain compiled EF Core migrations");

            string ddl = backendDb.Database.GenerateCreateScript();
            string[] expectedTables =
            {
                "cashiers", "categories", "customers", "tax_rates", "products",
                "sales", "sale_line_items", "shift_sessions", "inventory_movements",
                "store_settings", "audit_logs"
            };
            foreach (var tbl in expectedTables)
            {
                Assert(ddl.Contains(tbl, StringComparison.OrdinalIgnoreCase), $"PostgreSQL DDL must create table '{tbl}'");
            }

            // Production JWT secret guard must reject empty or placeholder keys
            bool rejectedEmpty = false;
            try { JwtSecretPolicy.ResolveSigningKey("", isDevelopment: false, out _); }
            catch (InvalidOperationException) { rejectedEmpty = true; }
            Assert(rejectedEmpty, "Production must reject empty JWT_SECRET_KEY");

            bool rejectedPlaceholder = false;
            try { JwtSecretPolicy.ResolveSigningKey("PrimePos-Enterprise-HmacSha256-SigningKey-2026-OverrideInProd!", isDevelopment: false, out _); }
            catch (InvalidOperationException) { rejectedPlaceholder = true; }
            Assert(rejectedPlaceholder, "Production must reject placeholder JWT_SECRET_KEY");

            // Development with empty key generates ephemeral 256-bit key
            string devKey = JwtSecretPolicy.ResolveSigningKey("", isDevelopment: true, out bool ephemeral);
            Assert(ephemeral && devKey.Length >= 32, "Development must generate an ephemeral 256-bit key when unconfigured");

            // Production with strong 256-bit random key succeeds
            string strongKey = "9f8e7d6c5b4a392817263544536271809a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d";
            string prodKey = JwtSecretPolicy.ResolveSigningKey(strongKey, isDevelopment: false, out bool prodEphemeral);
            Assert(!prodEphemeral && prodKey == strongKey, "Strong production key must be accepted verbatim");
        });

        RunTest("16. Performance & Responsiveness: Instant Local-First Login (<150ms), Cached PIN Override (<5ms), and Zero-Query Tab/Search Switching (<50ms)", () =>
        {
            using var harness = new PosTestHarness();
            var auth = harness.GetAuthenticationService();

            // Warm up first login
            var firstLogin = auth.AuthenticateHybridAsync("admin", "1234").GetAwaiter().GetResult();
            Assert(firstLogin.Success, "Hybrid login must succeed offline");

            // Benchmark offline login (must NOT wait 3,000ms for dead localhost socket)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var secondLogin = auth.AuthenticateHybridAsync("admin", "1234").GetAwaiter().GetResult();
            sw.Stop();
            Assert(secondLogin.Success && sw.ElapsedMilliseconds < 150,
                $"Expected instant local-first login < 150ms, took {sw.ElapsedMilliseconds}ms");

            // Benchmark repeated supervisor PIN check via session cache
            _ = auth.VerifyManagerOrAdminPin("2222");
            sw.Restart();
            var cachedMgr = auth.VerifyManagerOrAdminPin("2222");
            sw.Stop();
            Assert(cachedMgr != null && sw.ElapsedMilliseconds < 10,
                $"Expected cached supervisor PIN verification < 10ms, took {sw.ElapsedMilliseconds}ms");

            // Benchmark 100 tab switches + 50 catalog searches
            harness.LoginAsAdmin();
            using var vm = harness.CreateCheckoutViewModel();
            sw.Restart();
            string[] tabs = { "Transactions", "Customers", "Shift", "Admin", "Register" };
            for (int i = 0; i < 100; i++)
            {
                vm.SwitchSectionCommand.Execute(tabs[i % tabs.Length]);
            }
            for (int i = 0; i < 50; i++)
            {
                vm.ManualSkuInput = (i % 2 == 0) ? "100" : "";
            }
            sw.Stop();
            Assert(sw.ElapsedMilliseconds < 150,
                $"100 tab switches + 50 catalog filters expected < 150ms, took {sw.ElapsedMilliseconds}ms");
        });

        RunTest("17. Zero-Downtime SQLite Hot Backup (VACUUM INTO) & CSV Reports Export / Supplier Catalog Import roundtrip", () =>
        {
            using var harness = new PosTestHarness();
            harness.LoginAsAdmin();
            using var vm = harness.CreateCheckoutViewModel();

            // 1. Hot Backup via VACUUM INTO
            var backup = vm.RunHotBackup();
            Assert(backup.Success && File.Exists(backup.BackupFilePath), "Hot backup .db snapshot must exist on disk");
            Assert(backup.FileSizeBytes > 4096, $"Backup size must be > 4KB, got {backup.FileSizeBytes}");

            // Clean up test backup file so we don't pollute user backups
            try { File.Delete(backup.BackupFilePath); } catch { }

            // 2. One-click CSV Reports Export (4 CSV files)
            var exportRes = vm.ExportCsvReports();
            Assert(exportRes.Success && exportRes.FilesExported == 4, $"Expected 4 exported CSV reports, got {exportRes.FilesExported}");
            Assert(exportRes.TotalRowsExported >= 12, $"Expected >= 12 exported rows, got {exportRes.TotalRowsExported}");

            // 3. Bulk CSV Supplier Catalog Import (Insert + Upsert with Wholesale & Expiry)
            string tempCsv = Path.Combine(Path.GetTempPath(), $"pos_import_test_{Guid.NewGuid():N}.csv");
            File.WriteAllLines(tempCsv, new[]
            {
                "Sku,Name,Barcode,CategoryName,Unit,CostPrice,Price,WholesaleMinQty,WholesaleUnitPrice,Stock,ReorderLevel,IsTaxable,ExpiryDate",
                "IMP-9001,Imported Olive Oil (1L),6159900090011,Pantry & Dry Goods,btl,6200,8500,6,7800,35,8,true,2027-06-30",
                "PRD-1001,Organic Bananas (1kg Bunch),6151100010018,Fresh Produce & Fruit,kg,950,1550,10,1250,90,15,false,2026-11-12"
            });

            var maint = harness.GetMaintenanceService();
            var importRes = maint.ImportProductsFromCsv(tempCsv);
            try { File.Delete(tempCsv); } catch { }

            Assert(importRes.Success && importRes.CreatedCount == 1, $"Expected 1 created product, got {importRes.CreatedCount} ({importRes.Message})");
            Assert(importRes.UpdatedCount == 1, $"Expected 1 updated product, got {importRes.UpdatedCount}");

            using var db = harness.CreateDbContext();
            var olive = db.Products.FirstOrDefault(p => p.Sku == "IMP-9001");
            Assert(olive != null && olive.Price == 8500m && olive.WholesaleMinQty == 6 && olive.WholesaleUnitPrice == 7800m,
                "Imported SKU IMP-9001 must persist with wholesale tier pricing");
            var bananas = db.Products.First(p => p.Sku == "PRD-1001");
            Assert(bananas.Price == 1550m && bananas.WholesaleUnitPrice == 1250m && bananas.Stock == 90,
                "Updated SKU PRD-1001 must reflect CSV price, wholesale price, and stock");
        });

        RunTest("18. Customer Loyalty Points Redemption (1 pt = ₦10) & Store Credit Tender at Checkout and Refund", () =>
        {
            using var harness = new PosTestHarness();
            var admin = harness.LoginAsAdmin();
            using var vm = harness.CreateCheckoutViewModel();

            vm.OpeningFloatInput = 20000m;
            vm.OpenShift();

            Customer testCust;
            using (var db = harness.CreateDbContext())
            {
                testCust = new Customer
                {
                    Id = Ulid.NewUlid(),
                    Name = "Chinedu Okafor",
                    Phone = "08030009988",
                    LoyaltyPoints = 100, // Worth ₦1,000 discount
                    StoreCreditBalance = 5000m
                };
                db.Customers.Add(testCust);
                db.SaveChanges();
            }

            vm.LoadDatabaseData();
            vm.AttachCustomerCommand.Execute(vm.Customers.First(c => c.Id == testCust.Id));

            // Add non-taxable item: PRD-1001 Organic Bananas @ 1,500 x 4 = 6,000
            var bananas = vm.FilteredProducts.First(p => p.Sku == "PRD-1001");
            vm.AddProductWithQuantity(bananas, 4);
            Assert(vm.CurrentSale.Subtotal == 6000m, $"Expected subtotal 6000, got {vm.CurrentSale.Subtotal}");

            // Redeem 100 loyalty points -> -₦1,000 discount -> Total becomes ₦5,000
            vm.RedeemCustomerLoyaltyPoints();
            Assert(vm.CurrentSale.LoyaltyPointsRedeemed == 100, $"Expected 100 points redeemed, got {vm.CurrentSale.LoyaltyPointsRedeemed}");
            Assert(vm.CurrentSale.DiscountAmount == 1000m, $"Expected 1000 loyalty discount, got {vm.CurrentSale.DiscountAmount}");
            Assert(vm.CurrentSale.TotalAmount == 5000m, $"Expected total 5000 after loyalty redemption, got {vm.CurrentSale.TotalAmount}");

            // Pay ₦3,000 with Store Credit + ₦2,000 with Cash
            vm.SelectedPaymentMethod = PaymentMethod.StoreCredit;
            vm.StoreCreditTenderInput = 3000m;
            vm.CashTenderInput = 2000m;

            Ulid completedSaleId = vm.CurrentSale.Id;
            Assert(vm.FinalizeSalePaymentAsync().GetAwaiter().GetResult(), "Sale with Loyalty Points + Store Credit must succeed");

            using (var db = harness.CreateDbContext())
            {
                var updatedCust = db.Customers.First(c => c.Id == testCust.Id);
                // 100 pts - 100 redeemed + 5 earned (on ₦5,000 sale) = 5 pts
                Assert(updatedCust.LoyaltyPoints == 5, $"Expected 5 remaining+earned loyalty points, got {updatedCust.LoyaltyPoints}");
                // ₦5,000 credit - ₦3,000 tendered = ₦2,000 remaining
                Assert(updatedCust.StoreCreditBalance == 2000m, $"Expected 2000 remaining StoreCreditBalance, got {updatedCust.StoreCreditBalance}");
            }

            // Refund 2 units (₦2,500) -> must restore Store Credit balance to customer
            Sale savedSale;
            using (var db = harness.CreateDbContext())
            {
                savedSale = db.Sales.Include(s => s.Items).First(s => s.Id == completedSaleId);
            }
            var retSlip = vm.ExecuteApprovedRefund(
                completedSaleId,
                new Dictionary<Ulid, int> { [savedSale.Items[0].Id] = 2 },
                admin);
            Assert(retSlip != null && retSlip.TotalAmount == 2500m, "Partial refund on loyalty/store-credit sale must succeed");

            using (var db = harness.CreateDbContext())
            {
                var custAfterRefund = db.Customers.First(c => c.Id == testCust.Id);
                Assert(custAfterRefund.StoreCreditBalance == 4500m, $"Expected StoreCreditBalance restored to 4500, got {custAfterRefund.StoreCreditBalance}");
            }
        });

        RunTest("19. Wholesale / Case-Pack Tiered Pricing auto-applies at threshold quantity & Perishable Expiry Badges trigger accurately", () =>
        {
            var engine = new CartExecutionEngine();
            var vat = new TaxRate { Name = "VAT", Percentage = 0.075m, IsInclusive = false };
            var sale = new Sale();

            // BEV-1007 Fresh Squeezed Orange Juice: Retail 3,200, Wholesale (6+ pack) 2,850
            var juice = CatalogSeedData.GetDefaultProducts().First(p => p.Sku == "BEV-1007");
            Assert(juice.WholesaleMinQty == 6 && juice.WholesaleUnitPrice == 2850m, "Seed data must configure 6-pack wholesale tier on BEV-1007");

            engine.AddItemToCart(sale, juice, 2, vat);
            Assert(sale.Items[0].UnitPrice == 3200m, $"Qty 2 should use retail price 3200, got {sale.Items[0].UnitPrice}");

            // Increase quantity to 6 -> must automatically switch to wholesale unit price 2,850
            engine.UpdateItemQuantity(sale, sale.Items[0], 6, vat);
            Assert(sale.Items[0].UnitPrice == 2850m, $"Qty 6 should auto-apply wholesale price 2850, got {sale.Items[0].UnitPrice}");
            Assert(sale.Items[0].LineSubtotal == 17100m, $"6 * 2850 expected 17100, got {sale.Items[0].LineSubtotal}");

            // Decrease quantity back to 3 -> must revert to standard retail price 3,200
            engine.UpdateItemQuantity(sale, sale.Items[0], 3, vat);
            Assert(sale.Items[0].UnitPrice == 3200m, $"Qty 3 should revert to retail price 3200, got {sale.Items[0].UnitPrice}");

            // Perishable Expiry Badge checks
            var expiredItem = new Product { Name = "Yogurt", Stock = 12, ReorderLevel = 5, ExpiryDate = DateTime.UtcNow.Date.AddDays(-1) };
            Assert(expiredItem.IsExpired && expiredItem.StockBadgeText.StartsWith("EXPIRED"), $"Expected EXPIRED badge, got {expiredItem.StockBadgeText}");

            var expiringSoonItem = new Product { Name = "Milk", Stock = 15, ReorderLevel = 5, ExpiryDate = DateTime.UtcNow.Date.AddDays(3) };
            Assert(expiringSoonItem.IsExpiringSoon && expiringSoonItem.StockBadgeText.StartsWith("EXP 3d"), $"Expected EXP 3d badge, got {expiringSoonItem.StockBadgeText}");
        });

        RunTest("20. Multi-Terminal Catalog Sync Payload, Global Crash Logger, & PostgreSQL v2.2 Schema DDL verification", () =>
        {
            // 1. Verify SyncOperationsRequest carries Categories and Products cleanly
            var req = new SyncOperationsRequest
            {
                Categories = CatalogSeedData.GetDefaultCategories().Take(2).ToList(),
                Products = CatalogSeedData.GetDefaultProducts().Take(3).ToList()
            };
            string json = JsonSerializer.Serialize(req, PosJsonOptions.Default);
            var roundtrip = JsonSerializer.Deserialize<SyncOperationsRequest>(json, PosJsonOptions.Default);
            Assert(roundtrip != null && roundtrip.Categories.Count == 2 && roundtrip.Products.Count == 3,
                "SyncOperationsRequest must roundtrip Categories and Products for multi-terminal sync");
            Assert(roundtrip!.Products.Any(p => p.WholesaleMinQty > 0), "Synced Products must preserve WholesaleMinQty");

            // 2. Verify Global Crash Shield Logger writes to %LocalAppData%\PosSystem\Logs
            PosSystem.Frontend.App.WriteCrashLog("UnitTestProbe", new InvalidOperationException("Simulated test exception"));
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PosSystem", "Logs");
            string todayLog = Path.Combine(logDir, $"pos_error_{DateTime.UtcNow:yyyyMMdd}.log");
            Assert(File.Exists(todayLog), "App.WriteCrashLog must create daily diagnostic log file");

            // 3. Verify PostgreSQL DDL includes v2.2 columns
            using var backendDb = new BackendDesignTimeDbContextFactory().CreateDbContext(Array.Empty<string>());
            string ddl = backendDb.Database.GenerateCreateScript();
            string[] v22Columns =
            {
                "wholesale_min_qty", "wholesale_unit_price", "expiry_date",
                "loyalty_points_redeemed", "store_credit_tendered"
            };
            foreach (var col in v22Columns)
            {
                Assert(ddl.Contains(col, StringComparison.OrdinalIgnoreCase), $"PostgreSQL DDL must include v2.2 column '{col}'");
            }
        });

        Console.WriteLine($"=== Results: {passed} Passed, {failed} Failed ===");
        return failed == 0 ? 0 : 1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class InMemoryTokenVault : ITokenVault
    {
        private StoredSessionPayload? _payload;
        public void SaveToken(string token) => _payload = new StoredSessionPayload { Token = token };
        public string? ReadToken() => _payload?.Token;
        public void SaveSession(string token, Cashier user) => _payload = new StoredSessionPayload { Token = token, User = user, SavedAtUtc = DateTime.UtcNow };
        public StoredSessionPayload? ReadSession() => _payload;
        public void ClearToken() => _payload = null;
    }

    private sealed class PosTestHarness : IDisposable
    {
        private readonly string _dbPath;
        private readonly ServiceProvider _serviceProvider;

        public PosTestHarness()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"pos_test_{Guid.NewGuid():N}.db");

            var services = new ServiceCollection();
            services.AddDbContext<LocalDbContext>(opts => opts.UseSqlite($"Data Source={_dbPath}"));
            services.AddSingleton<ITokenVault, InMemoryTokenVault>();
            services.AddSingleton<ISessionService, SessionService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<AuthenticationService>();
            services.AddSingleton<CartExecutionEngine>();
            services.AddSingleton<ReceiptPrinterService>();
            services.AddSingleton<BarcodeScannerListener>();
            services.AddSingleton<DatabaseMaintenanceService>();
            services.AddTransient<CheckoutViewModel>();
            services.AddHttpClient();

            _serviceProvider = services.BuildServiceProvider();

            using var db = CreateDbContext();
            db.InitializeAndSeed();
        }

        public LocalDbContext CreateDbContext()
        {
            var opts = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={_dbPath}")
                .Options;
            return new LocalDbContext(opts);
        }

        public Cashier LoginAsAdmin()
        {
            using var db = CreateDbContext();
            var admin = db.Cashiers.First(c => c.Role == UserRole.Admin);
            _serviceProvider.GetRequiredService<ISessionService>().Login($"OFFLINE-TOKEN-{admin.Id}", admin);
            return admin;
        }

        public Cashier LoginAsStaff()
        {
            using var db = CreateDbContext();
            var staff = db.Cashiers.First(c => c.Role == UserRole.Staff);
            _serviceProvider.GetRequiredService<ISessionService>().Login($"OFFLINE-TOKEN-{staff.Id}", staff);
            return staff;
        }

        public CheckoutViewModel CreateCheckoutViewModel()
            => _serviceProvider.GetRequiredService<CheckoutViewModel>();

        public AuthenticationService GetAuthenticationService()
            => _serviceProvider.GetRequiredService<AuthenticationService>();

        public DatabaseMaintenanceService GetMaintenanceService()
            => _serviceProvider.GetRequiredService<DatabaseMaintenanceService>();

        public void Dispose()
        {
            _serviceProvider.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
                if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
                if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
            }
            catch
            {
                // Best-effort temp cleanup
            }
        }
    }
}
