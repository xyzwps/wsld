using static wsld.Core.I18n.Loc;

namespace wsld.Core.Models;

/// <summary>
/// 一次 `wslc pull` 的结果(功能 004 · AC3)。
/// wslc 的详细输出里有 `Status:` 这一行,能区分"真的下载了新镜像"和"本地本来就是最新",
/// 界面给用户的提示不一样,所以在这里把它解析出来。
/// </summary>
public sealed record PullResult(string Reference, bool IsUpToDate, string StatusLine)
{
    /// <summary>界面提示文案(功能 004 · AC8)。放在 Core 里是为了让文案也能被单测覆盖。</summary>
    public string Notice => IsUpToDate ? Fmt("pull.uptodate", Reference) : Fmt("pull.done", Reference);
}