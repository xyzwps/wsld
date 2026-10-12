using System.Text.Json;
using System.Text.Json.Nodes;
using WslcDesktop.Core.Models;
using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 解析 wslc --format json 输出(功能 001 · AC3,功能 003 · AC4)。
/// wslc 3.0.1 实际输出为 NDJSON:每行一个 JSON 对象,且无数据时输出为空。
/// 为稳健起见,同时兼容:标准 JSON 数组、美化缩进 JSON、单个对象。
/// </summary>
public static class WslcJsonParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<ContainerInfo> ParseContainers(string output) => Parse<ContainerInfo>(output, T("parser.type.containers"));

    public static IReadOnlyList<ImageInfo> ParseImages(string output) => Parse<ImageInfo>(output, T("parser.type.images"));

    private static IReadOnlyList<T> Parse<T>(string output, string typeLabel)
        where T : class
    {
        var text = output.Trim('\r', '\n', ' ', '\t', '\uFEFF');
        if (text.Length == 0) return Array.Empty<T>();

        if (TryParseWholeDocument<T>(text, out var whole)) return whole;

        // NDJSON:逐行解析,坏行报行号与原文片段。
        var lines = text.Split('\n');
        var items = new List<T>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            items.Add(DeserializeOrThrow<T>(line, i + 1, typeLabel));
        }
        return items;
    }

    private static bool TryParseWholeDocument<T>(string text, out IReadOnlyList<T> items)
        where T : class
    {
        items = Array.Empty<T>();
        if (text[0] is not ('[' or '{')) return false;
        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonArray array)
            {
                var list = new List<T>(array.Count);
                foreach (var element in array)
                {
                    list.Add(element?.Deserialize<T>(Options)
                        ?? throw new JsonException("数组中存在 null 元素"));
                }
                items = list;
                return true;
            }
            if (node is JsonObject obj)
            {
                items = new[] { obj.Deserialize<T>(Options) ?? throw new JsonException("空对象") };
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false; // 整体不是合法 JSON,按 NDJSON 逐行处理
        }
    }

    private static T DeserializeOrThrow<T>(string line, int lineNumber, string typeLabel)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, Options)
                ?? throw new JsonException("内容为 null");
        }
        catch (JsonException ex)
        {
            var snippet = line.Length > 120 ? line[..120] + "…" : line;
            throw new FormatException(
                Fmt("parser.line-invalid", lineNumber, typeLabel, ex.Message, snippet), ex);
        }
    }
}