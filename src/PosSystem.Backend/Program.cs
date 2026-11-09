using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PosSystem.Backend.Data;
using PosSystem.Shared.Data;
using PosSystem.Shared.Models;
using PosSystem.Shared.Security;
using PosSystem.Shared.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    PosJsonOptions.Configure(options.SerializerOptions);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

bool isDevelopment = builder.Environment.IsDevelopment();
string connectionString = JwtSecretPolicy.ResolveConnectionString(
    Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? builder.Configuration.GetConnectionString("DefaultConnection"),
    isDevelopment);

builder.Services.AddDbContext<BackendDbContext>(options =>
    options.UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention());

string rawJwtKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY")
    ?? builder.Configuration["Jwt:Key"]
    ?? string.Empty;

string jwtKey = JwtSecretPolicy.ResolveSigningKey(rawJwtKey, isDevelopment, out bool usedEphemeralDevKey);
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "PrimePOS";
var jwtExpiryHours = int.TryParse(builder.Configuration["Jwt:ExpiryHours"], out var hrs) ? hrs : 24;

builder.Services.AddSingleton(new JwtSettings(jwtKey, jwtIssuer, jwtExpiryHours));
builder.Services.AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, HmacJwtAuthenticationHandler>("Bearer", null);

builder.Services.AddAuthorization();

var app = builder.Build();

if (usedEphemeralDevKey)
{
    app.Logger.LogWarning("No JWT_SECRET_KEY configured in Development. Generated an ephemeral 256-bit key for this process lifetime.");
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<BackendDbContext>();

    int retries = 5;
    while (retries > 0)
    {
        try
        {
            dbContext.Database.Migrate();
            break;
        }
        catch
        {
            retries--;
            if (retries == 0) throw;
            Thread.Sleep(2000);
        }
    }

    if (!dbContext.TaxRates.Any())
        dbContext.TaxRates.AddRange(CatalogSeedData.GetDefaultTaxRates());

    if (!dbContext.Categories.Any())
        dbContext.Categories.AddRange(CatalogSeedData.GetDefaultCategories());

    if (!dbContext.Products.Any())
        dbContext.Products.AddRange(CatalogSeedData.GetDefaultProducts());

    if (!dbContext.Cashiers.Any())
        dbContext.Cashiers.AddRange(CatalogSeedData.GetDefaultCashiers());

    if (!dbContext.Customers.Any())
        dbContext.Customers.AddRange(CatalogSeedData.GetDefaultCustomers());

    if (!dbContext.StoreSettings.Any())
        dbContext.StoreSettings.AddRange(CatalogSeedData.GetDefaultStoreSettings());

    dbContext.SaveChanges();
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

// HEALTH CHECK
app.MapGet("/api/v1/health", () => Results.Ok(new
{
    Status = "Healthy",
    ServerTimeUtc = DateTime.UtcNow,
    Version = "2.1.0"
})).WithName("HealthCheck");

// AUTH ENDPOINTS
app.MapPost("/api/v1/auth/signup", [Authorize(Roles = "Admin,Manager")] async (SignupRequest req, BackendDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Pin))
        return Results.BadRequest(new { Error = "Username and PIN are required." });

    if (await db.Cashiers.AnyAsync(c => c.Username.ToLower() == req.Username.Trim().ToLower()))
        return Results.BadRequest(new { Error = "Username already exists." });

    var cashier = new Cashier
    {
        Id = Ulid.NewUlid(),
        Username = req.Username.Trim(),
        Name = req.Name.Trim(),
        PinHash = PasswordHasher.HashPin(req.Pin),
        Role = req.Role,
        IsActive = true,
        UpdatedAt = DateTime.UtcNow
    };

    db.Cashiers.Add(cashier);
    await db.SaveChangesAsync();

    return Results.Ok(cashier);
}).WithName("Signup");

app.MapPost("/api/v1/auth/login", async (LoginRequest req, BackendDbContext db, JwtSettings jwt) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Pin))
        return Results.Unauthorized();

    var normalized = req.Username.Trim().ToLower();
    var cashier = await db.Cashiers.FirstOrDefaultAsync(c => c.Username.ToLower() == normalized && c.IsActive);
    if (cashier == null || !PasswordHasher.VerifyPin(req.Pin, cashier.PinHash))
        return Results.Unauthorized();

    var expiresAtUtc = DateTime.UtcNow.AddHours(jwt.ExpiryHours);
    var tokenString = HmacJwtTokenHelper.CreateToken(cashier, jwt, expiresAtUtc);

    return Results.Ok(new LoginResponse
    {
        Token = tokenString,
        Cashier = cashier,
        ExpiresAtUtc = expiresAtUtc
    });
}).WithName("Login");

// TRANSACTIONAL OUTBOX SYNC ENDPOINT (SALES + PARTIAL/FULL RETURN SLIPS)
app.MapPost("/api/v1/sync/sales", [Authorize] async (List<Sale> sales, BackendDbContext db) =>
{
    var syncedIds = new List<string>();
    await using var tx = await db.Database.BeginTransactionAsync();

    foreach (var sale in sales)
    {
        bool exists = await db.Sales.AnyAsync(s => s.Id == sale.Id);
        if (exists)
        {
            syncedIds.Add(sale.Id.ToString());
            continue;
        }

        bool isReturnSlip = sale.Status == SaleStatus.Refunded && sale.OriginalSaleId.HasValue;

        foreach (var item in sale.Items)
        {
            item.SaleId = sale.Id;
            item.Sale = null;

            int unitUnits = item.MeasuredWeightKg.HasValue && item.MeasuredWeightKg.Value > 0m
                ? Math.Max(1, (int)Math.Ceiling(item.MeasuredWeightKg.Value)) * item.Quantity
                : item.Quantity;

            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == item.ProductId);
            if (product != null)
            {
                int delta = sale.Status == SaleStatus.Refunded ? unitUnits : -unitUnits;
                product.Stock += delta;
                product.UpdatedAt = DateTime.UtcNow;

                db.InventoryMovements.Add(new InventoryMovement
                {
                    Id = Ulid.NewUlid(),
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Type = sale.Status == SaleStatus.Refunded ? MovementType.Return : MovementType.Sale,
                    QuantityDelta = delta,
                    StockAfter = product.Stock,
                    ReferenceId = sale.ReceiptNumber,
                    PerformedBy = sale.CashierName,
                    Reason = $"Synced from {sale.TerminalId}",
                    CreatedAt = sale.CreatedAt
                });
            }
        }

        if (isReturnSlip && sale.OriginalSaleId.HasValue)
        {
            var originalSale = await db.Sales
                .Include(s => s.Items)
                .FirstOrDefaultAsync(s => s.Id == sale.OriginalSaleId.Value);

            if (originalSale != null)
            {
                foreach (var retItem in sale.Items)
                {
                    var origLine = originalSale.Items.FirstOrDefault(i =>
                        i.ProductId == retItem.ProductId && i.RemainingReturnableQuantity > 0);
                    if (origLine != null)
                    {
                        origLine.ReturnedQuantity = Math.Min(origLine.Quantity, origLine.ReturnedQuantity + retItem.Quantity);
                    }
                }

                originalSale.RefundedAmount = Math.Round(originalSale.RefundedAmount + sale.TotalAmount, 2, MidpointRounding.AwayFromZero);
                bool allReturned = originalSale.Items.All(i => i.ReturnedQuantity >= i.Quantity);
                originalSale.Status = allReturned ? SaleStatus.Refunded : SaleStatus.PartiallyRefunded;
            }
        }

        if (sale.CustomerId.HasValue)
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == sale.CustomerId.Value);
            if (customer != null)
            {
                if (sale.Status == SaleStatus.Completed)
                {
                    customer.TotalSpent += sale.TotalAmount;
                    customer.VisitCount += 1;
                    int redeemedPts = Math.Clamp(sale.LoyaltyPointsRedeemed, 0, customer.LoyaltyPoints);
                    int earnedPts = (int)Math.Floor(sale.TotalAmount / 1000m);
                    customer.LoyaltyPoints = Math.Max(0, customer.LoyaltyPoints - redeemedPts) + earnedPts;
                    if (sale.StoreCreditTendered > 0m)
                    {
                        customer.StoreCreditBalance = Math.Max(0m, customer.StoreCreditBalance - sale.StoreCreditTendered);
                    }
                    customer.UpdatedAt = DateTime.UtcNow;
                }
                else if (isReturnSlip)
                {
                    customer.TotalSpent = Math.Max(0m, customer.TotalSpent - sale.TotalAmount);
                    int pointsDeducted = (int)Math.Floor(sale.TotalAmount / 1000m);
                    customer.LoyaltyPoints = Math.Max(0, customer.LoyaltyPoints - pointsDeducted);
                    customer.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        db.Sales.Add(sale);
        syncedIds.Add(sale.Id.ToString());
    }

    await db.SaveChangesAsync();
    await tx.CommitAsync();

    return Results.Ok(new SyncSalesResponse
    {
        SyncedCount = syncedIds.Count,
        SyncedSaleIds = syncedIds,
        ServerTimeUtc = DateTime.UtcNow
    });
}).WithName("SyncSales");

// BIDIRECTIONAL OPERATIONS SYNC (CUSTOMERS, SHIFTS, AUDIT LOGS, STORE SETTINGS, CATALOG)
app.MapPost("/api/v1/sync/operations", [Authorize] async (SyncOperationsRequest req, BackendDbContext db) =>
{
    int syncedCustomers = 0;
    int syncedShifts = 0;
    int syncedAudits = 0;
    int syncedSettings = 0;
    int syncedCategories = 0;
    int syncedProducts = 0;

    await using var tx = await db.Database.BeginTransactionAsync();

    foreach (var customer in req.Customers)
    {
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Id == customer.Id);
        if (existing == null)
        {
            db.Customers.Add(customer);
            syncedCustomers++;
        }
        else if (customer.UpdatedAt >= existing.UpdatedAt)
        {
            existing.Name = customer.Name;
            existing.Phone = customer.Phone;
            existing.Email = customer.Email;
            existing.LoyaltyPoints = customer.LoyaltyPoints;
            existing.StoreCreditBalance = customer.StoreCreditBalance;
            existing.TotalSpent = Math.Max(existing.TotalSpent, customer.TotalSpent);
            existing.VisitCount = Math.Max(existing.VisitCount, customer.VisitCount);
            existing.UpdatedAt = customer.UpdatedAt;
            syncedCustomers++;
        }
    }

    foreach (var shift in req.ShiftSessions)
    {
        var existing = await db.ShiftSessions.FirstOrDefaultAsync(s => s.Id == shift.Id);
        if (existing == null)
        {
            db.ShiftSessions.Add(shift);
            syncedShifts++;
        }
        else
        {
            existing.ClosedAt = shift.ClosedAt;
            existing.CashSalesTotal = shift.CashSalesTotal;
            existing.CardSalesTotal = shift.CardSalesTotal;
            existing.TransferSalesTotal = shift.TransferSalesTotal;
            existing.RefundsTotal = shift.RefundsTotal;
            existing.CashDropsTotal = shift.CashDropsTotal;
            existing.ExpectedCash = shift.ExpectedCash;
            existing.ActualCountedCash = shift.ActualCountedCash;
            existing.Variance = shift.Variance;
            existing.TotalTransactions = shift.TotalTransactions;
            existing.Notes = shift.Notes;
            existing.IsClosed = shift.IsClosed;
            syncedShifts++;
        }
    }

    foreach (var audit in req.AuditLogs)
    {
        bool exists = await db.AuditLogs.AnyAsync(a => a.Id == audit.Id);
        if (!exists)
        {
            audit.IsSynced = true;
            db.AuditLogs.Add(audit);
            syncedAudits++;
        }
    }

    foreach (var setting in req.StoreSettings)
    {
        var existing = await db.StoreSettings.FirstOrDefaultAsync(s => s.Key == setting.Key);
        if (existing == null)
        {
            db.StoreSettings.Add(setting);
            syncedSettings++;
        }
        else if (setting.UpdatedAt >= existing.UpdatedAt)
        {
            existing.Value = setting.Value;
            existing.UpdatedAt = setting.UpdatedAt;
            syncedSettings++;
        }
    }

    foreach (var cat in req.Categories)
    {
        var existing = await db.Categories.FirstOrDefaultAsync(c => c.Id == cat.Id);
        if (existing == null)
        {
            db.Categories.Add(cat);
            syncedCategories++;
        }
        else if (cat.UpdatedAt >= existing.UpdatedAt)
        {
            existing.Name = cat.Name;
            existing.Description = cat.Description;
            existing.ColorHex = cat.ColorHex;
            existing.IsActive = cat.IsActive;
            existing.UpdatedAt = cat.UpdatedAt;
            syncedCategories++;
        }
    }

    foreach (var prod in req.Products)
    {
        var existing = await db.Products.FirstOrDefaultAsync(p => p.Id == prod.Id || p.Sku == prod.Sku);
        if (existing == null)
        {
            db.Products.Add(prod);
            syncedProducts++;
        }
        else if (prod.UpdatedAt >= existing.UpdatedAt)
        {
            existing.CategoryId = prod.CategoryId;
            existing.Name = prod.Name;
            existing.Sku = prod.Sku;
            existing.Barcode = prod.Barcode;
            existing.Unit = prod.Unit;
            existing.CostPrice = prod.CostPrice;
            existing.Price = prod.Price;
            existing.WholesaleMinQty = prod.WholesaleMinQty;
            existing.WholesaleUnitPrice = prod.WholesaleUnitPrice;
            existing.ExpiryDate = prod.ExpiryDate;
            existing.IsTaxable = prod.IsTaxable;
            existing.Stock = prod.Stock;
            existing.ReorderLevel = prod.ReorderLevel;
            existing.IsActive = prod.IsActive;
            existing.UpdatedAt = prod.UpdatedAt;
            syncedProducts++;
        }
    }

    await db.SaveChangesAsync();
    await tx.CommitAsync();

    return Results.Ok(new SyncOperationsResponse
    {
        SyncedCustomers = syncedCustomers,
        SyncedShifts = syncedShifts,
        SyncedAuditLogs = syncedAudits,
        SyncedSettings = syncedSettings,
        SyncedCategories = syncedCategories,
        SyncedProducts = syncedProducts,
        ServerTimeUtc = DateTime.UtcNow
    });
}).WithName("SyncOperations");

// BIDIRECTIONAL CATALOG DELTA SYNC
app.MapGet("/api/v1/sync/catalog", [Authorize] async (DateTime? since, BackendDbContext db) =>
{
    var categoriesQuery = db.Categories.AsQueryable();
    var productsQuery = db.Products.AsQueryable();
    var taxRatesQuery = db.TaxRates.AsQueryable();
    var cashiersQuery = db.Cashiers.AsQueryable();
    var customersQuery = db.Customers.AsQueryable();
    var settingsQuery = db.StoreSettings.AsQueryable();

    if (since.HasValue)
    {
        var utcSince = DateTime.SpecifyKind(since.Value, DateTimeKind.Utc);
        categoriesQuery = categoriesQuery.Where(c => c.UpdatedAt >= utcSince);
        productsQuery = productsQuery.Where(p => p.UpdatedAt >= utcSince);
        taxRatesQuery = taxRatesQuery.Where(t => t.UpdatedAt >= utcSince);
        cashiersQuery = cashiersQuery.Where(c => c.UpdatedAt >= utcSince);
        customersQuery = customersQuery.Where(c => c.UpdatedAt >= utcSince);
        settingsQuery = settingsQuery.Where(s => s.UpdatedAt >= utcSince);
    }

    var cashiers = await cashiersQuery
        .Select(c => new CashierSyncDto
        {
            Id = c.Id,
            Username = c.Username,
            Name = c.Name,
            PinHash = c.PinHash,
            Role = c.Role,
            IsActive = c.IsActive,
            UpdatedAt = c.UpdatedAt
        })
        .ToListAsync();

    return Results.Ok(new CatalogSyncResponse
    {
        Categories = await categoriesQuery.ToListAsync(),
        Products = await productsQuery.ToListAsync(),
        TaxRates = await taxRatesQuery.ToListAsync(),
        Cashiers = cashiers,
        Customers = await customersQuery.ToListAsync(),
        StoreSettings = await settingsQuery.ToListAsync(),
        ServerTimeUtc = DateTime.UtcNow
    });
}).WithName("GetCatalogDeltas");

// BACKWARDS-COMPATIBLE PRODUCT DELTA ENDPOINT
app.MapGet("/api/v1/products/delta", [Authorize] async (DateTime? since, BackendDbContext db) =>
{
    var query = db.Products.AsQueryable();
    if (since.HasValue)
    {
        var utcSince = DateTime.SpecifyKind(since.Value, DateTimeKind.Utc);
        query = query.Where(p => p.UpdatedAt >= utcSince);
    }

    var products = await query.ToListAsync();
    return Results.Ok(products);
}).WithName("GetProductDeltas");

app.Run();

public static class JwtSecretPolicy
{
    private static readonly string[] DisallowedFragments =
    {
        "overrideinprod",
        "changeme",
        "replace_with",
        "default_secret",
        "password123"
    };

    public static string ResolveConnectionString(string? configuredConnectionString, bool isDevelopment)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
            return configuredConnectionString.Trim();

        if (isDevelopment)
            return "Host=localhost;Port=5433;Database=PosBackend;Username=pos_admin;Password=LocalDevOnly_ChangeMe";

        throw new InvalidOperationException(
            "Production database connection string is missing. Configure ConnectionStrings__DefaultConnection via environment variables or secret store.");
    }

    public static string ResolveSigningKey(string? configuredKey, bool isDevelopment, out bool usedEphemeralDevKey)
    {
        usedEphemeralDevKey = false;
        string trimmed = configuredKey?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            if (isDevelopment)
            {
                usedEphemeralDevKey = true;
                return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            }

            throw new InvalidOperationException(
                "JWT signing key is missing in non-Development environment. Set JWT_SECRET_KEY (minimum 32 characters).");
        }

        if (Encoding.UTF8.GetByteCount(trimmed) < 32)
        {
            throw new InvalidOperationException(
                "JWT signing key must be at least 32 bytes (256 bits) for HMAC-SHA256 security.");
        }

        string lower = trimmed.ToLowerInvariant();
        if (!isDevelopment && DisallowedFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Placeholder or weak JWT signing key detected in non-Development environment. Provide a cryptographically random JWT_SECRET_KEY.");
        }

        return trimmed;
    }
}

// Built-in RFC 7519 HMAC-SHA256 JWT Helper & ASP.NET Core AuthenticationHandler
public record JwtSettings(string Key, string Issuer, int ExpiryHours);

public static class HmacJwtTokenHelper
{
    public static string CreateToken(Cashier cashier, JwtSettings settings, DateTime expiresAtUtc)
    {
        string headerJson = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
        var payload = new Dictionary<string, object>
        {
            ["sub"] = cashier.Id.ToString(),
            ["unique_name"] = cashier.Username,
            ["display_name"] = cashier.Name,
            ["role"] = cashier.Role.ToString(),
            ["iss"] = settings.Issuer,
            ["aud"] = settings.Issuer,
            ["exp"] = new DateTimeOffset(expiresAtUtc).ToUnixTimeSeconds()
        };
        string payloadJson = JsonSerializer.Serialize(payload);

        string unsignedToken = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))}";
        byte[] sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.Key), Encoding.UTF8.GetBytes(unsignedToken));
        return $"{unsignedToken}.{Base64UrlEncode(sig)}";
    }

    public static ClaimsPrincipal? ValidateToken(string token, JwtSettings settings)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        string unsignedToken = $"{parts[0]}.{parts[1]}";
        byte[] expectedSig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.Key), Encoding.UTF8.GetBytes(unsignedToken));
        byte[] actualSig = Base64UrlDecode(parts[2]);

        if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig))
            return null;

        using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var root = doc.RootElement;

        if (root.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var expUnix))
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expUnix + 120)
                return null;
        }

        var claims = new List<Claim>();
        if (root.TryGetProperty("sub", out var sub)) claims.Add(new Claim(ClaimTypes.NameIdentifier, sub.GetString() ?? ""));
        if (root.TryGetProperty("unique_name", out var name)) claims.Add(new Claim(ClaimTypes.Name, name.GetString() ?? ""));
        if (root.TryGetProperty("role", out var role)) claims.Add(new Claim(ClaimTypes.Role, role.GetString() ?? ""));

        var identity = new ClaimsIdentity(claims, "Bearer");
        return new ClaimsPrincipal(identity);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}

public sealed class HmacJwtAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly JwtSettings _jwtSettings;

    public HmacJwtAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        JwtSettings jwtSettings)
        : base(options, logger, encoder)
    {
        _jwtSettings = jwtSettings;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return Task.FromResult(AuthenticateResult.NoResult());

        string headerVal = authHeader.ToString();
        if (!headerVal.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        string token = headerVal["Bearer ".Length..].Trim();
        try
        {
            var principal = HmacJwtTokenHelper.ValidateToken(token, _jwtSettings);
            if (principal == null)
                return Task.FromResult(AuthenticateResult.Fail("Invalid or expired JWT token."));

            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception ex)
        {
            return Task.FromResult(AuthenticateResult.Fail(ex.Message));
        }
    }
}
