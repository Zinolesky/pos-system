using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public class SaleLineItem
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid SaleId { get; set; }

    [JsonIgnore]
    public Sale? Sale { get; set; }

    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid ProductId { get; set; }

    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }
    public int ReturnedQuantity { get; set; }
    public decimal? MeasuredWeightKg { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal CostPrice { get; set; }
    public decimal LineDiscountAmount { get; set; }
    public bool IsTaxable { get; set; } = true;

    public decimal LineSubtotal { get; set; }
    public decimal LineTax { get; set; }
    public decimal LineTotal { get; set; }

    [JsonIgnore]
    public bool IsNegativeStockAuthorized { get; set; }

    [NotMapped]
    [JsonIgnore]
    public decimal StandardRetailPrice { get; set; }

    [NotMapped]
    [JsonIgnore]
    public int WholesaleMinQty { get; set; }

    [NotMapped]
    [JsonIgnore]
    public decimal WholesaleUnitPrice { get; set; }

    [NotMapped]
    [JsonIgnore]
    public bool IsManualPriceOverride { get; set; }

    [JsonIgnore]
    public int RemainingReturnableQuantity => Math.Max(0, Quantity - ReturnedQuantity);
}
