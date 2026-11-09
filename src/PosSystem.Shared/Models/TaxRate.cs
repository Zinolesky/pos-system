using System;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public class TaxRate
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    public string Name { get; set; } = "VAT (7.5%)";
    public decimal Percentage { get; set; } = 0.075m;
    public bool IsInclusive { get; set; } = false;
    public bool IsDefault { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string DisplayLabel => IsInclusive
        ? $"{Name} ({Percentage * 100:0.##}% Incl.)"
        : $"{Name} ({Percentage * 100:0.##}%)";
}
