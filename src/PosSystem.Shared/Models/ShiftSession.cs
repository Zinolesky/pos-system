using System;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public class ShiftSession
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    public string TerminalId { get; set; } = "TERM-01";

    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid CashierId { get; set; }
    public string CashierName { get; set; } = string.Empty;

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    public decimal OpeningFloat { get; set; }
    public decimal CashSalesTotal { get; set; }
    public decimal CardSalesTotal { get; set; }
    public decimal TransferSalesTotal { get; set; }
    public decimal RefundsTotal { get; set; }
    public decimal CashDropsTotal { get; set; }

    public decimal ExpectedCash { get; set; }
    public decimal? ActualCountedCash { get; set; }
    public decimal? Variance { get; set; }
    public int TotalTransactions { get; set; }
    public string? Notes { get; set; }
    public bool IsClosed { get; set; }
}
