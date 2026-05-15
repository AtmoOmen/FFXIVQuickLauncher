using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XIVLauncher.Settings.Converters;

public sealed class DirectoryInfoJsonConverter : JsonConverter<DirectoryInfo>
{
    public override DirectoryInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : new DirectoryInfo(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, DirectoryInfo value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.FullName);
}
