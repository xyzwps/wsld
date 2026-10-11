using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslcDesktop.Core.Models;

/// <summary>容器平台信息(wslc list 输出中的 Platform 字段)。</summary>
public sealed class PlatformInfo
{
    public string Architecture { get; set; } = "";
    public string OS { get; set; } = "";
}

/// <summary>
/// 一个容器的列表视图数据,字段与 wslc 3.0.1 的真实输出对齐
/// (功能 001 · AC4,样例见 tests/WslcDesktop.Tests/Fixtures)。
/// </summary>
public sealed class ContainerInfo
{
    public string Id { get; set; } = "";
    public string Names { get; set; } = "";
    public string Image { get; set; } = "";
    public string Command { get; set; } = "";
    public string Status { get; set; } = "";
    public string State { get; set; } = "";
    public string RunningFor { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Ports { get; set; } = "";
    public string Networks { get; set; } = "";
    public string Labels { get; set; } = "";
    public string Size { get; set; } = "";
    public string HealthStatus { get; set; } = "";
    public PlatformInfo? Platform { get; set; }

    /// <summary>State 为 running(不区分大小写)视为运行中(AC5)。</summary>
    [JsonIgnore]
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names 可能是逗号分隔的别名列表;取第一个,并去掉 docker 风格的开头 '/'。</summary>
    [JsonIgnore]
    public string DisplayName =>
        Names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .FirstOrDefault()?.TrimStart('/')
             ?? "";

    [JsonIgnore]
    public string ShortId => Id.Length >= 12 ? Id[..12] : Id;

    [JsonIgnore]
    public string ImageLine => $"{Image}  ·  {Command}";

    [JsonIgnore]
    public string SubtleLine => $"状态 {State} · ID {ShortId} · 网络 {DashIfEmpty(Networks)} · 端口 {DashIfEmpty(Ports)}";

    private static string DashIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}