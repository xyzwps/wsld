using System.Text.RegularExpressions;

namespace wsld.Core.Models;

/// <summary>
/// 一次拉取"此刻"的进度快照(功能 011 · AC2)。
/// 数据来自对 wslc 终端屏幕行的解析:每个分层的最新一行状态、层总数、已完成层数与已下载字节。
/// </summary>
public sealed record PullProgressSnapshot(
    IReadOnlyList<string> Lines,
    int TotalLayers,
    int DoneLayers,
    long DownloadedBytes)
{
    /// <summary>屏幕上一行状态都没有(刚启动、还没拿到 Manifest)时为 true。</summary>
    public bool IsEmpty => Lines.Count == 0;
}
