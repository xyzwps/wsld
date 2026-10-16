using wsld.Core.Models;
using wsld.Core.Parsing;
using wsld.Core.WslcCli;

namespace wsld.Core.Services;

/// <summary>
/// 镜像相关的 wslc 调用。
/// 功能 003:列表查询;功能 004:拉取与删除;功能 010:打标签(镜像源改名);功能 011:拉取实时进度。
/// </summary>
public sealed class ImagesService
{
    /// <summary>强制删除参数(功能 004 · 实测只有 -f 能删掉被容器引用的镜像)。</summary>
    public const string ForceFlag = "-f";

    private readonly IWslcCli _cli;

    public ImagesService(IWslcCli cli)
    {
        _cli = cli;
    }

    /// <summary>列出本机镜像,对应 `wslc images --format json`(功能 003 · AC1)。</summary>
    public async Task<IReadOnlyList<ImageInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await _cli.RunAsync(new[] { "images", "--format", "json" }, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException("wslc images", result);
        }
        return WslcJsonParser.ParseImages(result.StdOut);
    }

    /// <summary>
    /// 拉取镜像,对应 `wslc pull &lt;引用&gt;`(功能 004 · AC1)。
    /// 不用 `-q`:实测详细输出的 `Status:` 行能区分"新下载"和"已是最新"。
    /// 传给这里的引用就是要拉的那个 —— 镜像源改写(功能 010)发生在上层,服务层不掺逻辑。
    /// </summary>
    public async Task<PullResult> PullAsync(string reference, CancellationToken cancellationToken = default)
    {
        var target = RequireReference(reference);
        var result = await _cli.RunAsync(new[] { "pull", target }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException("wslc pull", result);
        }
        return WslcPullLog.Parse(target, result.StdOut);
    }

    /// <summary>
    /// 带实时进度的拉取(功能 011 · AC1/AC4):命令、结果解析、失败判定与 <see cref="PullAsync"/> 完全一致,
    /// 只是走伪终端,所以分层进度能边下边上屏;<paramref name="onVisibleLines"/> 在后台读线程上被调用。
    /// 保留 <see cref="PullAsync"/> 不动,是因为服务层与集成测试都还在用它验证同一条命令映射(AC5)。
    /// </summary>
    public async Task<PullResult> PullWithProgressAsync(
        string reference,
        Action<IReadOnlyList<string>>? onVisibleLines = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireReference(reference);
        var result = await _cli.RunWithConsoleAsync(new[] { "pull", target }, onVisibleLines, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException("wslc pull", result);
        }
        return WslcPullLog.Parse(target, result.StdOut);
    }

    /// <summary>
    /// 给已有镜像补一个标签,对应 `wslc tag &lt;源引用&gt; &lt;目标引用&gt;`(功能 010 · AC5)。
    /// 实测退出码 0、无任何输出;源与目标都先校验非空,再启动进程。
    /// </summary>
    public async Task TagAsync(string source, string target, CancellationToken cancellationToken = default)
    {
        var from = RequireReference(source);
        var to = RequireReference(target);

        var result = await _cli.RunAsync(new[] { "tag", from, to }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException("wslc tag", result);
        }
    }

    /// <summary>
    /// 删除镜像引用,对应 `wslc rmi [-f] &lt;引用&gt;`(功能 004 · AC4)。
    /// 实测 `rmi` 一次传多个引用时遇到第一个失败就停手,所以这里坚持"一次只删一个引用"。
    /// </summary>
    public async Task RemoveAsync(string reference, bool force = false, CancellationToken cancellationToken = default)
    {
        var target = RequireReference(reference);
        var arguments = force
            ? new string[] { "rmi", ForceFlag, target }
            : new string[] { "rmi", target };

        var result = await _cli.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException("wslc rmi", result);
        }
    }

    /// <summary>
    /// 空引用属于不可挽回操作的输入错误,必须在启动进程之前挡掉(功能 004 · AC2)。
    /// </summary>
    private static string RequireReference(string? reference)
    {
        var trimmed = reference?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("镜像引用不能为空。", nameof(reference));
        }
        return trimmed;
    }
}