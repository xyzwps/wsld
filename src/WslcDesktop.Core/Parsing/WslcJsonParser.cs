using System.Text.Json;
using System.Text.Json.Nodes;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 解析 wslc --format json 输出(功能 001 · AC3)。
/// wslc 3.0.1 实际输出为 NDJSON:每行一个 JSON 对象,且无数据时输出为空。
/// 为稳健起见,同时兼容:标准 JSON 数组、美化缩进 JSON、单个对象。
/// </summary>
public static class WslcJsonParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<ContainerInfo> ParseContainers(string output)
    {
        var text = output.Trim('\r', '\n', ' ', '\t', '\uFEFF');
        if (text.Length == 0) return Array.Empty<ContainerInfo>();

        if (TryParseWholeDocument(text, out var whole)) return whole;

        // NDJSON:逐行解析,坏行报行号与原文片段。
        var lines = text.Split('\n');
        var items = new List<ContainerInfo>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            items.Add(DeserializeOrThrow(line, lineNumber: i + 1));
        }
        return items;
    }

    private static bool TryParseWholeDocument(string text, out IReadOnlyList<ContainerInfo> items)
    {
        items = Array.Empty<ContainerInfo>();
        if (text[0] is not ('[' or '{')) return false;
        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonArray array)
            {
                var list = new List<ContainerInfo>(array.Count);
                foreach (var element in array)
                {
                    list.Add(element?.Deserialize<ContainerInfo>(Options)
                        ?? throw new JsonException("数组中存在 null 元素"));
                }
                items = list;
                return true;
            }
            if (node is JsonObject obj)
            {
                items = new[] { obj.Deserialize<ContainerInfo>(Options) ?? throw new JsonException("空对象") };
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false; // 整体不是合法 JSON,按 NDJSON 逐行处理
        }
    }

    private static ContainerInfo DeserializeOrThrow(string line, int lineNumber)
    {
        try
        {
            return JsonSerializer.Deserialize<ContainerInfo>(line, Options)
                ?? throw new JsonException("内容为 null");
        }
        catch (JsonException ex)
        {
            var snippet = line.Length > 120 ? line[..120] + "…" : line;
            throw new FormatException(
                $"wslc 输出第 {lineNumber} 行不是有效的容器 JSON:{ex.Message}(原文片段:{snippet})", ex);
        }
    }
}