using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PosSystem.Shared.Serialization;

public sealed class UlidJsonConverter : JsonConverter<Ulid>
{
    public override Ulid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                if (Ulid.TryParse(str, out var ulid))
                    return ulid;
                if (Guid.TryParse(str, out var guid))
                    return new Ulid(guid);
            }
        }
        return Ulid.Empty;
    }

    public override void Write(Utf8JsonWriter writer, Ulid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

public sealed class NullableUlidJsonConverter : JsonConverter<Ulid?>
{
    public override Ulid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return null;
            if (Ulid.TryParse(str, out var ulid))
                return ulid;
            if (Guid.TryParse(str, out var guid))
                return new Ulid(guid);
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, Ulid? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString());
        else
            writer.WriteNullValue();
    }
}

public static class PosJsonOptions
{
    public static JsonSerializerOptions Default { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNameCaseInsensitive = true
        };
        Configure(options);
        return options;
    }

    public static void Configure(JsonSerializerOptions options)
    {
        options.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        options.PropertyNameCaseInsensitive = true;
        options.Converters.Add(new UlidJsonConverter());
        options.Converters.Add(new NullableUlidJsonConverter());
    }
}
