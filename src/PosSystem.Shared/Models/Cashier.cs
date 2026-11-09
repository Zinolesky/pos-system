using System;
using System.Text.Json.Serialization;
using PosSystem.Shared.Serialization;

namespace PosSystem.Shared.Models;

public enum UserRole
{
    Staff = 0,
    Manager = 1,
    Admin = 2
}

public class Cashier
{
    [JsonConverter(typeof(UlidJsonConverter))]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    public string Username { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public string PinHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Staff;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool CanManageOrVoid => Role == UserRole.Admin || Role == UserRole.Manager;
}
