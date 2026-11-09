using System;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public enum MovementType
{
    Sale = 0,
    Restock = 1,
    Return = 2,
    Adjustment = 3,
    Damage = 4
}

public class InventoryMovement
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;

    public MovementType Type { get; set; }
    public int QuantityDelta { get; set; }
    public int StockAfter { get; set; }
    public string? ReferenceId { get; set; }
    public string PerformedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
