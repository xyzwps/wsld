using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>功能 003 · AC1:镜像服务的命令拼装与错误传播。</summary>
public class ImagesServiceTests
{
    [Fact]
    public async Task ListAsync_InvokesWslcWithMachineReadableArgs()
    {
        var cli = new FakeWslcCli().Enqueue(0, "[]");
        var service = new ImagesService(cli);

        await service.ListAsync();

        var args = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "images", "--format", "json" }, args);
    }

    [Fact]
    public async Task ListAsync_EmptyOutput_ReturnsEmptyList()
    {
        var cli = new FakeWslcCli().Enqueue(0, "");
        var service = new ImagesService(cli);

        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task ListAsync_ReturnsParsedImages()
    {
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-images.ndjson"));
        var service = new ImagesService(cli);

        var items = await service.ListAsync();

        Assert.Equal(2, items.Count);
        Assert.Equal("docker.1ms.run/library/busybox:latest", items[0].Reference);
    }

    [Fact]
    public async Task ListAsync_NonZeroExit_ThrowsWithStderrMessage()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", "错误代码： E_FAIL");
        var service = new ImagesService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => service.ListAsync());
        Assert.Contains("E_FAIL", ex.Message);
        Assert.Contains("wslc images", ex.Message);
    }

    [Fact]
    public async Task ListAsync_Cancellation_IsPropagated()
    {
        var cli = new CancelledCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ListAsync(Cancelled.Token));
    }

    private static CancellationTokenSource Cancelled { get; } = CreateCancelled();

    private static CancellationTokenSource CreateCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts;
    }

    private sealed class CancelledCli : IWslcCli
    {
        public Task<WslcCliResult> RunLinesAsync(IReadOnlyList<string> arguments, Action<string>? onStdoutLine, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        // 这些闸门假 CLI 只关心"调用有没有挂起",用不到实时上屏,所以伪终端路径同样转给一次性实现。
        public Task<WslcCliResult> RunWithConsoleAsync(IReadOnlyList<string> arguments, Action<IReadOnlyList<string>>? onVisibleLines, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        public Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WslcCliResult(0, "", ""));
        }
    }
}