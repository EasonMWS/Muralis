using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muralis.Core.Models;

/// <summary>The two supported product-level relationships between Muralis and Windows.</summary>
[JsonConverter(typeof(DesktopExperienceModeJsonConverter))]
public enum DesktopExperienceMode
{
    /// <summary>Windows keeps its native desktop and icon presentation.</summary>
    Native,

    /// <summary>
    /// Windows still owns desktop content, while Muralis hides the native icons and presents that
    /// content through its required Dock and Desktop Shelf.
    /// </summary>
    Muralis,
}

/// <summary>
/// Reads former product values without keeping them in the public enum. CleanDesktop becomes
/// Muralis; the retired takeover and every unknown value fail open to Native.
/// </summary>
public sealed class DesktopExperienceModeJsonConverter : JsonConverter<DesktopExperienceMode>
{
    public override DesktopExperienceMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, "Muralis", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "CleanDesktop", StringComparison.OrdinalIgnoreCase))
            {
                return DesktopExperienceMode.Muralis;
            }

            return DesktopExperienceMode.Native;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
        {
            // The former CleanDesktop value was 1, which is also the new Muralis value.
            return numeric == 1 ? DesktopExperienceMode.Muralis : DesktopExperienceMode.Native;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return DesktopExperienceMode.Native;
        }

        using var ignored = JsonDocument.ParseValue(ref reader);
        return DesktopExperienceMode.Native;
    }

    public override void Write(Utf8JsonWriter writer, DesktopExperienceMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == DesktopExperienceMode.Muralis ? "Muralis" : "Native");
}

public sealed class DesktopExperienceSettings
{
    /// <summary>Safe by default: a fresh or downgraded installation leaves Windows untouched.</summary>
    public DesktopExperienceMode Mode { get; set; } = DesktopExperienceMode.Native;
}
