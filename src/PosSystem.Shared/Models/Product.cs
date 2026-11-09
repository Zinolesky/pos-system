using System;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public class Product
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid CategoryId { get; set; }

    [JsonIgnore]
    public Category? Category { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string Unit { get; set; } = "ea";

    public decimal CostPrice { get; set; }
    public decimal Price { get; set; }

    public int WholesaleMinQty { get; set; }
    public decimal WholesaleUnitPrice { get; set; }
    public DateTime? ExpiryDate { get; set; }

    public bool IsTaxable { get; set; } = true;
    public string? ImageUrl { get; set; }

    public int Stock { get; set; }
    public int ReorderLevel { get; set; } = 10;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsLowStock => Stock > 0 && Stock <= ReorderLevel;

    [JsonIgnore]
    public bool IsOutOfStock => Stock <= 0;

    [JsonIgnore]
    public bool IsExpired => ExpiryDate.HasValue && ExpiryDate.Value.Date < DateTime.UtcNow.Date;

    [JsonIgnore]
    public bool IsExpiringSoon =>
        ExpiryDate.HasValue &&
        !IsExpired &&
        (ExpiryDate.Value.Date - DateTime.UtcNow.Date).TotalDays <= 7;

    [JsonIgnore]
    public string StockBadgeText => IsExpired
        ? $"EXPIRED ({Stock} {Unit})"
        : IsExpiringSoon
            ? $"EXP {(int)Math.Max(0, (ExpiryDate!.Value.Date - DateTime.UtcNow.Date).TotalDays)}d ({Stock})"
            : Stock < 0
                ? $"NEG: {Stock} {Unit}"
                : Stock == 0
                    ? "OUT OF STOCK"
                    : Stock <= ReorderLevel
                        ? $"Low: {Stock} {Unit}"
                        : $"{Stock} in stock";
}
