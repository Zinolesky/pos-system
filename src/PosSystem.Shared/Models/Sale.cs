using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
    BankTransfer = 2,
    Split = 3,
    StoreCredit = 4
}

public enum SaleStatus
{
    Completed = 0,
    Held = 1,
    Suspended = 1,
    Refunded = 2,
    PartiallyRefunded = 3,
    Voided = 4
}

public class Sale
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    public string ReceiptNumber { get; set; } = GenerateReceiptNumber();
    public string TerminalId { get; set; } = "TERM-01";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonConverter(typeof(NullableUlidJsonConverter))]
    public Ulid? ShiftId { get; set; }

    [JsonConverter(typeof(NullableUlidJsonConverter))]
    public Ulid? CashierId { get; set; }
    public string CashierName { get; set; } = string.Empty;

    [JsonConverter(typeof(NullableUlidJsonConverter))]
    public Ulid? CustomerId { get; set; }
    public string? CustomerName { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DiscountPercentage { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxRatePercentage { get; set; } = 0.075m;
    public bool IsTaxInclusive { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal RefundedAmount { get; set; }

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public decimal CashTendered { get; set; }
    public decimal CardTendered { get; set; }
    public decimal TransferTendered { get; set; }
    public decimal AmountTendered { get; set; }
    public decimal ChangeDue { get; set; }
    public string? PaymentReference { get; set; }

    public SaleStatus Status { get; set; } = SaleStatus.Completed;

    [JsonConverter(typeof(NullableUlidJsonConverter))]
    public Ulid? OriginalSaleId { get; set; }
    public string? Notes { get; set; }

    public List<SaleLineItem> Items { get; set; } = new();

    [JsonIgnore]
    public bool CanRefund =>
        (Status == SaleStatus.Completed || Status == SaleStatus.PartiallyRefunded) &&
        OriginalSaleId == null;

    public static string GenerateReceiptNumber()
    {
        var now = DateTime.UtcNow;
        var suffix = Ulid.NewUlid().ToString()[^6..];
        return $"RCP-{now:yyyyMMdd}-{suffix}";
    }
}
