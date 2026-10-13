using System.Text.RegularExpressions;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 把"屏幕上的行"解析成结构化的拉取进度(功能 011 · AC2)。
/// 实测行形态(hello-world / ubuntu / python 三例观察):
///   "24.04: Pulling from library/ubuntu"          —— 头部信息行
///   "a60ef8ec184e: Pulling fs layer"              —— 分层事件行
///   "a60ef8ec184e: Downloading 544.5kB"           —— 字节数随刷新递增(kB/MB/GB,小数可变)
///   "a60ef8ec184e: Verifying Checksum"            —— 瞬时事件,无字节
///   "a60ef8ec184e: Extracting 33.3%"              —— 解压阶段(也可能是 "Extracting 5.5MB/27MB")
///   "a60ef8ec184e: Pull complete" / "Already exists" —— 终态
///   "Digest: sha256:..." / "Status: ..." / 完整引用  —— 收尾三行
/// 解析不了的行不算错误,只是不进统计;终态判定与 <see cref="WslcPullLog"/> 一致地保守。
/// </summary>
public static partial class WslcPullProgress
{
    /// <summary>分层行:12~64 位十六进制 + 冒号 + 空格 + 状态。</summary>
    [GeneratedRegex(@"^([0-9a-fA-F]{12,64}): (.+)$")]
    private static partial Regex LayerLineRegex();

    /// <summary>状态里的字节数:先找"数字+可选小数+单位";"x/y"形态取分母(全量)。</summary>
    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*([kmgt]?)b(?:\s*/\s*([0-9]+(?:\.[0-9]+)?)\s*([kmgt]?)b)?", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    /// <summary>
    /// 这行是不是某个分层的状态行(界面与错误文案要用它把"进度"和"报错"分开)。
    /// </summary>
    public static bool IsLayerLine(string line) => LayerLineRegex().IsMatch(line);

    /// <summary>
    /// 解析"屏幕上此刻可见的行",得到层数 / 完成数 / 已下载字节(功能 011 · AC2)。
    /// <paramref name="layerHighWaterBytes"/> 是可选的"每层已下载字节历史最大值"台账:
    /// 调用方在一次拉取期间传同一个字典进来,开始下一次拉取时清空。
    /// 为什么要台账 —— 真机观测:某层变成 <c>Download complete</c> 之后那一行不再带字节数,
    /// 只按当前帧求和会让"已下载"从 1.6 MB 退回 545.6 kB 甚至归零,数字倒退比不显示更糟。
    /// 给了台账就按台账累计(只增不减);不给则保持"只看当前帧"的老行为。
    /// </summary>
    public static PullProgressSnapshot Parse(
        IReadOnlyList<string> lines,
        IDictionary<string, long>? layerHighWaterBytes = null)
    {
        var layerStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var layerBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var orderedLayers = new List<string>();

        foreach (var line in lines)
        {
            var match = LayerLineRegex().Match(line);
            if (!match.Success) continue;

            var id = match.Groups[1].Value.ToLowerInvariant();
            var status = match.Groups[2].Value.Trim();
            if (!layerStates.ContainsKey(id)) orderedLayers.Add(id);
            layerStates[id] = status;

            var size = SizeRegex().Match(status);
            if (size.Success && (status.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase)
                              || status.StartsWith("Extracting", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = ToBytes(size);
                if (bytes > 0 && layerBytes.TryGetValue(id, out var known))
                {
                    layerBytes[id] = Math.Max(known, bytes);
                }
                else if (bytes > 0)
                {
                    layerBytes[id] = bytes;
                }
            }
        }

        var done = 0;
        foreach (var state in layerStates.Values)
        {
            if (IsDoneState(state)) done++;
        }

        var downloaded = 0L;
        if (layerHighWaterBytes is not null)
        {
            // 台账:当前帧里不再报字节数的层(下载完之后)依然按历史最大值计入,所以总量单调不减。
            foreach (var pair in layerBytes)
            {
                if (!layerHighWaterBytes.TryGetValue(pair.Key, out var known) || pair.Value > known)
                {
                    layerHighWaterBytes[pair.Key] = pair.Value;
                }
            }

            foreach (var bytes in layerHighWaterBytes.Values)
            {
                downloaded += bytes;
            }
        }
        else
        {
            downloaded = layerBytes.Values.Sum();
        }

        return new PullProgressSnapshot(lines, layerStates.Count, done, downloaded);
    }

    /// <summary>"Download complete" 只是下载完成,不算层完成;真正终态是这两个。</summary>
    private static bool IsDoneState(string status) =>
        status.StartsWith("Pull complete", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("Already exists", StringComparison.OrdinalIgnoreCase);

    /// <summary>正则分组 → 字节数;x/y 形态取分母(总量),没有分母取分子。</summary>
    private static long ToBytes(Match size)
    {
        var numerator = ParseSingle(size.Groups[1].Value, size.Groups[2].Value);
        if (size.Groups[3].Success)
        {
            return ParseSingle(size.Groups[3].Value, size.Groups[4].Value) ?? numerator ?? 0;
        }

        return numerator ?? 0;
    }

    private static long? ParseSingle(string number, string unit)
    {
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var factor = unit.ToLowerInvariant() switch
        {
            "k" => 1_000L,
            "m" => 1_000_000L,
            "g" => 1_000_000_000L,
            "t" => 1_000_000_000_000L,
            _ => 1L,
        };

        return (long)(value * factor);
    }

    /// <summary>
    /// 把字节数格式化成人眼友好的 "12.3 MB"(界面用,与 wslc 自己的单位风格一致,按 1000 进制)。
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = ["B", "kB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        var text = unit == 0 ? ((long)value).ToString() : value.ToString("0.#");
        return $"{text} {units[unit]}";
    }
}
