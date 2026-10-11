using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 解析 `wslc pull` 的详细输出(功能 004 · AC3)。
/// 实测形态(hello-world,退出码 0):
///   latest: Pulling from library/hello-world
///   ...各层的 Pulling fs layer / Download complete / Extracting / Pull complete...
///   Digest: sha256:...
///   Status: Downloaded newer image for docker.1ms.run/library/hello-world:latest
///   docker.1ms.run/library/hello-world:latest
/// 已存在时 Status 行是 `Image is up to date for ...`。
/// 找不到 Status 行不算错误:返回"不是最新"的空状态即可(例如以后改用 -q)。
/// </summary>
public static class WslcPullLog
{
    public const string StatusPrefix = "Status:";
    public const string UpToDateMarker = "up to date";

    public static PullResult Parse(string reference, string? output)
    {
        var status = "";
        if (!string.IsNullOrWhiteSpace(output))
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.StartsWith(StatusPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    status = line[StatusPrefix.Length..].Trim();
                }
            }
        }

        var upToDate = status.Contains(UpToDateMarker, StringComparison.OrdinalIgnoreCase);
        return new PullResult(reference, upToDate, status);
    }
}