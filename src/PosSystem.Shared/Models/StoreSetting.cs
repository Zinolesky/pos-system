using System;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public enum InventorySellPolicy
{
    BlockOnZeroStock = 0,
    WarnAndRequireManagerOverride = 1,
    AllowNegativeStock = 2
}

public class StoreSetting
{
    public const string InventoryPolicyKey = "InventorySellPolicy";

    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
