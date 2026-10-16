using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace wsld.Core.Models;

/// <summary>
/// 宽松字符串读取器(功能 003 · AC3)。
/// wslc 3.0.1 的 images JSON 里数字是以字符串给出的("Containers":"2"),
/// 但 CLI 随时可能改成真正的 JSON 数字,这个转换器让两种写法都能读。
/// </summary>
public sealed class LooseStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "",
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => "",
            _ => "",
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    private static string ReadNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var longValue)) return longValue.ToString(CultureInfo.InvariantCulture);
        if (reader.TryGetDecimal(out var decimalValue)) return decimalValue.ToString(CultureInfo.InvariantCulture);
        return reader.TryGetDouble(out var doubleValue)
            ? doubleValue.ToString("0.####", CultureInfo.InvariantCulture)
            : "";
    }
}