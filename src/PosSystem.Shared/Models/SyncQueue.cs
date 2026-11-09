using System;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public class SyncQueue
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid SaleId { get; set; }

    [JsonIgnore]
    public Sale? Sale { get; set; }

    public string EntityType { get; set; } = "Sale";
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public int RetryCount { get; set; }
    public string? LastError { get; set; }

    [JsonIgnore]
    public string? ErrorMessage
    {
        get => LastError;
        set => LastError = value;
    }

    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? SyncedAt { get; set; }
}
