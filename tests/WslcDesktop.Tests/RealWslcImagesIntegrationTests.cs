using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 003 / 004 的真实 CLI 冒烟。
/// 默认跳过,设置了 WSLC_DESKTOP_INTEGRATION=1 才运行。
/// 这些用例都挑"不会破坏本机"的入口:重复拉取已存在的镜像、删除正被引用的镜像(引擎会拒绝)、
/// 以及删除一个临时标签(只取消标签,不动镜像数据)。
/// </summary>
public class RealWslcImagesIntegrationTests
{
    private const string Enabled = "WSLC_DESKTOP_INTEGRATION";
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