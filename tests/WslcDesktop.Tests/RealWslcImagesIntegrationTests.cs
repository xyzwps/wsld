using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 003 的真实 CLI 冒烟:只读取,不创建不删除任何东西。
/// 默认跳过,设置了 WSLC_DESKTOP_INTEGRATION=1 才运行。
/// </summary>
public class RealWslcImagesIntegrationTests
{
    private const string Enabled = "WSLC_DESKTOP_INTEGRATION";

    [Fact]
    public async Task ListAsync_OnRealWslc_ReturnsParsedImages()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return;

        var path = WslcLocator.Find();
        Assert.NotNull(path);

        var images = await new ImagesService(new ProcessWslcCli(path)).ListAsync();

        Assert.NotEmpty(images);
        Assert.All(images, image =>
        {
            Assert.NotEmpty(image.Id);
            Assert.NotEmpty(image.Reference);      // AC5:有标签用 仓库:标签,没标签退化成 ID
            Assert.DoesNotContain("<none>:<none>", image.Reference);
        });
    }
}