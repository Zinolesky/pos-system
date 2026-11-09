using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System;
using PosSystem.Shared.Models;

namespace PosSystem.Backend.Data;

public class UlidToGuidConverter : ValueConverter<Ulid, Guid>
{
    public UlidToGuidConverter()
        : base(
            v => v.ToGuid(),
            v => new Ulid(v))
    {
    }
}
