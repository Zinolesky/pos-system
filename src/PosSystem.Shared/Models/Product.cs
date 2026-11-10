using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public class Product : INotifyPropertyChanged
{
    private int _stock;
    private int _reservedInCart;

    public event PropertyChangedEventHandler? PropertyChanged;

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

    public int Stock
    {
        get => _stock;
        set
        {
            if (_stock != value)
            {
                _stock = value;
                NotifyStockChanged();
            }
        }
    }

    public int ReorderLevel { get; set; } = 10;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    [JsonIgnore]
    public int ReservedInCart
    {
        get => _reservedInCart;
        set
        {
            if (_reservedInCart != value)
            {
                _reservedInCart = value;
                NotifyStockChanged();
            }
        }
    }

    [NotMapped]
    [JsonIgnore]
    public int EffectiveStock => Stock - ReservedInCart;

    [NotMapped]
    [JsonIgnore]
    public bool IsLowStock => EffectiveStock > 0 && EffectiveStock <= ReorderLevel;

    [NotMapped]
    [JsonIgnore]
    public bool IsOutOfStock => EffectiveStock <= 0;

    [NotMapped]
    [JsonIgnore]
    public bool IsInStock => EffectiveStock > 0;

    [NotMapped]
    [JsonIgnore]
    public bool IsExpired => ExpiryDate.HasValue && ExpiryDate.Value.Date < DateTime.UtcNow.Date;

    [NotMapped]
    [JsonIgnore]
    public bool IsExpiringSoon =>
        ExpiryDate.HasValue &&
        !IsExpired &&
        (ExpiryDate.Value.Date - DateTime.UtcNow.Date).TotalDays <= 7;

    [NotMapped]
    [JsonIgnore]
    public string StockBadgeText => IsExpired
        ? $"EXPIRED ({EffectiveStock} {Unit})"
        : IsExpiringSoon
            ? $"EXP {(int)Math.Max(0, (ExpiryDate!.Value.Date - DateTime.UtcNow.Date).TotalDays)}d ({EffectiveStock})"
            : EffectiveStock < 0
                ? $"NEG: {EffectiveStock} {Unit}"
                : EffectiveStock == 0
                    ? "OUT OF STOCK"
                    : EffectiveStock <= ReorderLevel
                        ? $"Low: {EffectiveStock} {Unit}"
                        : $"{EffectiveStock} in stock";

    [NotMapped]
    [JsonIgnore]
    public string StockBadgeForeground => IsOutOfStock || IsExpired
        ? "#F87171"
        : (IsLowStock || IsExpiringSoon ? "#FBBF24" : "#38BDF8");

    private void NotifyStockChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveStock)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLowStock)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOutOfStock)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInStock)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StockBadgeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StockBadgeForeground)));
    }
}
