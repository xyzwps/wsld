using System.Globalization;
using System.Text.Json.Serialization;
using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Core.Models;

/// <summary>
/// 一个镜像的列表视图数据,字段与 wslc 3.0.1 `images --format json` 的真实输出对齐
/// (功能 003 · AC2,样例见 tests/WslcDesktop.Tests/Fixtures/wslc-images.ndjson)。
/// 同一镜像 ID 有多个标签时,wslc 会为每个"仓库:标签"输出一行,因此这里一行就是一个引用。
///
/// 所有字符串字段都归一化成非 null:JSON 里出现 null 或缺字段时留空串,
/// 免得每个显示属性都要各自防 null。
/// </summary>
public sealed class ImageInfo
{
    private string _id = "";
    private string _repository = "";
    private string _tag = "";
    private string _size = "";
    private string _createdAt = "";
    private string _createdSince = "";
    private string _containers = "";
    private string _digest = "";
    private string _sharedSize = "";
    private string _uniqueSize = "";

    [JsonConverter(typeof(LooseStringConverter))]
    public string Id { get => _id; set => _id = Text(value); }

    [JsonConverter(typeof(LooseStringConverter))]
    public string Repository { get => _repository; set => _repository = Text(value); }

    [JsonConverter(typeof(LooseStringConverter))]
    public string Tag { get => _tag; set => _tag = Text(value); }

    [JsonConverter(typeof(LooseStringConverter))]
    public string Size { get => _size; set => _size = Text(value); }

    [JsonConverter(typeof(LooseStringConverter))]
    public string CreatedAt { get => _createdAt; set => _createdAt = Text(value); }

    [JsonConverter(typeof(LooseStringConverter))]
    public string CreatedSince { get => _createdSince; set => _createdSince = Text(value); }

    /// <summary>wslc 用字符串表示引用它的容器数量("2");无法识别时按 0 处理(AC3)。</summary>
    [JsonConverter(typeof(LooseStringConverter))]
    public string Containers { get => _containers; set => _containers = Text(value); }

    [JsonConverter(typeof(LooseStringConverter))]
    public string Digest { get => _digest; set => _digest = Text(value); }

    [JsonConverter(typeof(LooseStringConverter))]
    public string SharedSize { get => _sharedSize; set => _sharedSize = Text(value); }

    [JsonConverter(typeof(LooseStringConverter))]
    public string UniqueSize { get => _uniqueSize; set => _uniqueSize = Text(value); }

    /// <summary>引用该镜像的容器数量,解析失败为 0。</summary>
    [JsonIgnore]
    public int ContainerCount =>
        int.TryParse(Containers.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : 0;

    [JsonIgnore]
    public bool IsInUse => ContainerCount > 0;

    /// <summary>有真实仓库名才算"有标签";空、"&lt;none&gt;"、"-" 都视为无标签(AC5)。</summary>
    [JsonIgnore]
    public bool IsTagged => !IsPlaceholder(Repository);

    /// <summary>功能 004 删除镜像时使用的引用名;无标签镜像退化为镜像 ID。</summary>
    [JsonIgnore]
    public string Reference => !IsTagged ? Id : TagText.Length == 0 ? Repository : $"{Repository}:{TagText}";

    /// <summary>去掉 "&lt;none&gt;" 之后的标签文本,没有标签时为空串。</summary>
    [JsonIgnore]
    public string TagText => IsPlaceholder(Tag) ? "" : Tag.Trim();

    [JsonIgnore]
    public string ShortId => Id.Length >= 12 ? Id[..12] : Id;

    /// <summary>主标题:有标签时"仓库:标签",无标签时"无标签 · 短 ID"(AC5)。</summary>
    [JsonIgnore]
    public string DisplayName => IsTagged
        ? (TagText.Length == 0 ? Repository : $"{Repository}:{TagText}")
        : Fmt("image.untagged", ShortId);

    /// <summary>主行右侧的大小文本(AC6)。</summary>
    [JsonIgnore]
    public string SizeText => DashIfEmpty(Size);

    /// <summary>主行右侧的引用情况(AC6)。</summary>
    [JsonIgnore]
    public string UsageText => IsInUse ? Fmt("image.in-use", ContainerCount) : T("image.not-in-use");

    /// <summary>次行:ID、创建时间,存在真实摘要时补一段(AC6)。</summary>
    [JsonIgnore]
    public string SubtleLine =>
        Fmt("image.subtle", ShortId, CreatedText)
        + (IsPlaceholder(Digest) ? "" : Fmt("image.digest-suffix", Digest.Trim()));

    /// <summary>创建时间:优先把 wslc 的原文解析成本地可读时间,失败则退回相对时间或原文(AC6)。</summary>
    [JsonIgnore]
    public string CreatedText => TryParseCreatedAt(CreatedAt, out var parsed)
        ? parsed.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
        : !string.IsNullOrWhiteSpace(CreatedSince)
            ? CreatedSince.Trim()
            : DashIfEmpty(CreatedAt);

    private static bool TryParseCreatedAt(string raw, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // 真实形态:"2026-05-13 10:21:49 +0800 GMT+8" —— 末尾的时区名会让解析失败,只取前三段。
        var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidate = parts.Length >= 3 ? string.Join(' ', parts[..3]) : raw.Trim();
        return DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            || DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static string Text(string? value) => value ?? "";

    private static bool IsPlaceholder(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() is "<none>" or "<null>" or "-";

    private static string DashIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
}