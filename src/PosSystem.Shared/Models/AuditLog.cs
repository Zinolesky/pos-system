using System;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public class AuditLog
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    public string TerminalId { get; set; } = "TERM-01";
    public string ActionType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    [JsonConverter(typeof(NullableUlidJsonConverter))]
    public Ulid? PerformedById { get; set; }
    public string PerformedByName { get; set; } = string.Empty;

    [JsonConverter(typeof(NullableUlidJsonConverter))]
    public Ulid? AuthorizedById { get; set; }
    public string AuthorizedByName { get; set; } = string.Empty;

    public string? ReferenceId { get; set; }
    public bool IsSynced { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
