using wsld.Core.Services;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>
/// 功能 003 / 004 的真实 CLI 冒烟。
/// 默认跳过,设置了 WSLD_INTEGRATION=1 才运行。
/// 这些用例都挑"不会破坏本机"的入口:重复拉取已存在的镜像、删除正被引用的镜像(引擎会拒绝)、
/// 以及删除一个临时标签(只取消标签,不动镜像数据)。
/// </summary>
public class RealWslcImagesIntegrationTests
{
    private const string Enabled = "WSLD_INTEGRATION";
    private const string TempReference = "wslcd-integration:temp";

    private static ProcessWslcCli? CliOrNull()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return null;
        var path = WslcLocator.Find();
        Assert.NotNull(path);
        return new ProcessWslcCli(path);
    }

    [Fact]
    public async Task ListAsync_OnRealWslc_ReturnsParsedImages()
    {
        var cli = CliOrNull();
        if (cli is null) return;

        var images = await new ImagesService(cli).ListAsync();

        Assert.NotEmpty(images);
        Assert.All(images, image =>
        {
            Assert.NotEmpty(image.Id);
            Assert.NotEmpty(image.Reference);      // AC5:有标签用 仓库:标签,没标签退化成 ID
            Assert.DoesNotContain("<none>:<none>", image.Reference);
        });
    }

    [Fact]
    public async Task PullAsync_OnExistingImage_ReportsUpToDate()
    {
        var cli = CliOrNull();
        if (cli is null) return;

        var service = new ImagesService(cli);
        var target = (await service.ListAsync()).First(i => i.IsTagged).Reference;

        var result = await service.PullAsync(target);

        // AC3:实测重复拉取会输出 `Status: Image is up to date for X`,据此区分"新下载"和"已是最新"。
        Assert.Equal(target, result.Reference);
        Assert.True(result.IsUpToDate, $"真实输出未标记为最新:{result.StatusLine}");
        Assert.Contains("已是最新", result.Notice);
    }

    [Fact]
    public async Task RemoveAsync_OnInUseImage_IsRejectedAsConflict()
    {
        var cli = CliOrNull();
        if (cli is null) return;

        var service = new ImagesService(cli);
        var inUse = (await service.ListAsync()).FirstOrDefault(i => i.IsInUse);
        if (inUse is null) return; // 本机没有在用的镜像,这条就不适用(引擎会拒绝的前提是不存在的)

        var failure = await Assert.ThrowsAsync<WslcCliException>(
            () => service.RemoveAsync(inUse.Reference));

        // AC10:只有这类"正在使用"的失败才会升级成"强制删除"。
        Assert.True(ImageRemovalConflict.IsConflict(failure), failure.Message);
        Assert.Equal("wslc rmi", failure.Command);

        // 被拒绝之后镜像必须还在(这条用例对本机完全无损)。
        Assert.Contains(
            inUse.Reference,
            (await service.ListAsync()).Select(i => i.Reference));
    }

    /// <summary>
    /// 功能 011 · AC1/AC5:伪终端路径的真实冒烟。挑"重复拉取已存在的镜像"这条无损快路径(~2 秒,只有 manifest 检查)。
    /// 它同时是 ConPtySession 的回归哨兵:实测 stdin 写端提前关闭会让 ConPTY 向子进程广播 Ctrl+C,
    /// wslc 一帧不画就带着 0xC000013A 退场 —— 那条路径任何单测都盖不到(单测走假 CLI),只能真机验。
    /// </summary>
    [Fact]
    public async Task PullWithProgressAsync_OnRealWslc_StreamsScreenThroughThePseudoTerminal()
    {
        var cli = CliOrNull();
        if (cli is null) return;

        var service = new ImagesService(cli);
        var target = (await service.ListAsync()).First(i => i.IsTagged).Reference;

        var frames = 0;
        string? lastScreen = null;
        var result = await service.PullWithProgressAsync(target, lines =>
        {
            Interlocked.Increment(ref frames);
            lastScreen = string.Join("\n", lines);
        });

        // 退出码非 0 会在 PullWithProgressAsync 里抛异常,能走到这里说明伪终端路径"活着跑完了"。
        Assert.True(frames >= 1, "伪终端一帧都没有回调 —— 子进程可能启动即被杀(见 ConPtySession 的输入写端注释)");
        Assert.NotNull(lastScreen);
        Assert.Contains("Status:", lastScreen);               // 收尾三行必须在最终屏幕里(AC4 的解析源)
        Assert.True(result.IsUpToDate, result.StatusLine);    // 已存在的镜像应判为"已是最新"(与 PullAsync 同一套解析)
    }

    [Fact]
    public async Task RemoveAsync_OnTempTag_DeletesOnlyTheTag()

    {
        var cli = CliOrNull();
        if (cli is null) return;

        var service = new ImagesService(cli);
        var source = (await service.ListAsync()).First(i => i.IsTagged).Reference;

        // 借真实 CLI 给已有镜像打一个临时标签,再删掉它:只取消标签,镜像数据不受影响。
        var tagged = await cli.RunAsync(new[] { "tag", source, TempReference }, CancellationToken.None);
        Assert.Equal(0, tagged.ExitCode);

        try
        {
            Assert.Contains(TempReference, (await service.ListAsync()).Select(i => i.Reference));

            await service.RemoveAsync(TempReference); // AC4:不带 -f 也能删,因为没有容器引用这个标签

            Assert.DoesNotContain(TempReference, (await service.ListAsync()).Select(i => i.Reference));
        }
        finally
        {
            await cli.RunAsync(new[] { "rmi", TempReference }, CancellationToken.None); // 幂等清理
        }
    }
}