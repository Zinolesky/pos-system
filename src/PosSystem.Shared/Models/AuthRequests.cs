using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public class SignupRequest
{
    public string Username { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Pin { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Staff;
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Pin { get; set; } = string.Empty;
}

public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public Cashier? Cashier { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public class CashierSyncDto
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PinHash { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

public class CatalogSyncResponse
{
    public List<Category> Categories { get; set; } = new();
    public List<Product> Products { get; set; } = new();
    public List<TaxRate> TaxRates { get; set; } = new();
    public List<CashierSyncDto> Cashiers { get; set; } = new();
    public List<Customer> Customers { get; set; } = new();
    public List<StoreSetting> StoreSettings { get; set; } = new();
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
}

public class SyncSalesResponse
{
    public int SyncedCount { get; set; }
    public List<string> SyncedSaleIds { get; set; } = new();
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
}

public class SyncOperationsRequest
{
    public List<Customer> Customers { get; set; } = new();
    public List<ShiftSession> ShiftSessions { get; set; } = new();
    public List<AuditLog> AuditLogs { get; set; } = new();
    public List<StoreSetting> StoreSettings { get; set; } = new();
}

public class SyncOperationsResponse
{
    public int SyncedCustomers { get; set; }
    public int SyncedShifts { get; set; }
    public int SyncedAuditLogs { get; set; }
    public int SyncedSettings { get; set; }
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
}
