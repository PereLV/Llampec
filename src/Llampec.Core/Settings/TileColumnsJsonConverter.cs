using System.Text.Json;
using System.Text.Json.Serialization;

namespace Llampec.Settings;

/// <summary>An invalid new layout field must not discard an otherwise usable settings file.</summary>
internal sealed class TileColumnsJsonConverter : JsonConverter<int>
{
    public TileColumnsJsonConverter() { }
    public override bool HandleNull => true;

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out int columns)) return TileLayout.NormalizeColumns(columns);
            if (reader.TryGetDouble(out double numeric))
            {
                if (numeric < TileLayout.MinimumColumns) return TileLayout.MinimumColumns;
                if (numeric > TileLayout.MaximumColumns) return TileLayout.MaximumColumns;
            }
        }
        reader.Skip();
        return TileLayout.DefaultColumns;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(TileLayout.NormalizeColumns(value));
}
