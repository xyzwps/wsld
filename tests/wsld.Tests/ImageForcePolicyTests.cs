using wsld.Core.Models;
using wsld.Core.Services;
using wsld.Core.ViewModels;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>
/// 功能 004 · AC10/AC11:强制删除的策略。
/// 实测只有 `wslc rmi -f` 才能删掉被容器引用的镜像,而它会把镜像真的从磁盘上拿走、
/// 让引用它的容器再也起不来,所以这个参数只能由"常规删除失败 + 用户第二次确认"触发。
/// </summary>
public class ImageForcePolicyTests
{
    private const string BusyBoxRow = """{"ID":"aaef90e06523","Repository":"docker.1ms.run/library/busybox","Tag":"latest","Size":"4.45MB","Containers":"2","CreatedAt":"2026-05-13 10:21:49 +0800 GMT+8","Digest":"<none>"}""";
    private const string HelloRow = """{"ID":"e2ac70e7319a","Repository":"docker.1ms.run/library/hello-world","Tag":"latest","Size":"10.1kB","Containers":"0","CreatedAt":"2026-03-24 05:33:59 +0800 GMT+8","Digest":"<none>"}""";
    private const string TwoRows = BusyBoxRow + "\n" + HelloRow;
    private const string BusyBoxRef = "docker.1ms.run/library/busybox:latest";

    /// <summary>实测原文:删除被容器引用的镜像时 wslc 给的冲突信息。</summary>
    private const string ConflictStdErr = """
        conflict: unable to remove repository reference "docker.1ms.run/library/busybox:latest" (must force) - container f47fdddf9219 is using its referenced image aaef90e06523

        错误代码： ERROR_SHARING_VIOLATION
        如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。
        """;

    /// <summary>实测原文:引用不存在。</summary>
    private const string NotFoundStdErr = """
        来自引擎的错误信息: No such image: nope-004:latest
        错误代码： WSLC_E_IMAGE_NOT_FOUND
        如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。
        """;

    // ---------- AC10 · 冲突识别 ----------

    [Theory]
    [InlineData(ConflictStdErr, true)]
    [InlineData("""container x is using its referenced image y (must force)""", true)]
    [InlineData("""MUST FORCE""", true)]
    [InlineData("""错误代码： ERROR_SHARING_VIOLATION""", true)]
    [InlineData("""unable to remove repository reference""", true)]
    [InlineData(NotFoundStdErr, false)]
    [InlineData("""错误代码： E_FAIL""", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ConflictDetection_OnlyMatchesRealOccupancyFailures(string? text, bool expected) =>
        Assert.Equal(expected, ImageRemovalConflict.Contains(text));

    [Fact]
    public void ConflictDetection_ReadsStdoutTooAndIgnoresForeignExceptions()
    {
        var inStdOut = new WslcCliException("wslc rmi", new WslcCliResult(1, ConflictStdErr, ""));
        Assert.True(ImageRemovalConflict.IsConflict(inStdOut));

        var unrelated = new WslcCliException("wslc rmi", new WslcCliResult(1, "", NotFoundStdErr));
        Assert.False(ImageRemovalConflict.IsConflict(unrelated));

        Assert.False(ImageRemovalConflict.IsConflict(new InvalidOperationException(ConflictStdErr)));
    }

    // ---------- AC10 · 被占用 → 按钮升级为"强制删除" ----------

    [Fact]
    public async Task Remove_Conflict_FirstAttemptSendsNoForceAndUpgradesTheRow()
    {
        var cli = new RoutingWslcCli { ListJson = TwoRows }.AlwaysFor("rmi", 1, "", ConflictStdErr);
        var vm = Create(cli);
        var row = vm.Items[0];

        var ok = await vm.ExecuteRemoveAsync(row);

        Assert.False(ok);
        Assert.Equal(new[] { "rmi", BusyBoxRef }, cli.LastCall("rmi"));   // 第一次绝不带 -f
        Assert.True(row.ForcePending);
        Assert.Equal("强制删除", row.RemoveLabel);
        Assert.Contains("ERROR_SHARING_VIOLATION", vm.Error);
        Assert.Contains(ImagesPageViewModel.ForceHint, vm.Error);          // 告诉用户下一步怎么做
        Assert.DoesNotContain("如果此错误是意外错误", vm.Error);             // 样板提示已清洗
        Assert.Equal(2, vm.Items.Count);                                   // 数据没丢
    }

    [Fact]
    public async Task Remove_OtherFailureDoesNotOfferTheDestructiveOption()
    {
        var cli = new RoutingWslcCli { ListJson = TwoRows }.AlwaysFor("rmi", 1, "", NotFoundStdErr);
        var vm = Create(cli);
        var row = vm.Items[0];

        await vm.ExecuteRemoveAsync(row);

        Assert.False(row.ForcePending);                 // AC10:非冲突失败不升级按钮
        Assert.Equal("删除", row.RemoveLabel);
        Assert.DoesNotContain("强制删除", vm.Error);
        Assert.Contains("WSLC_E_IMAGE_NOT_FOUND", vm.Error);
    }

    // ---------- AC11 · 第二次显式动作才真的强制 ----------

    [Fact]
    public async Task SecondRemoveFromTheRowItselfSendsForceAndResetsTheState()
    {
        var cli = new RoutingWslcCli { ListJson = TwoRows };
        cli.ActionHandler = (args, _) =>
        {
            // 模拟真实引擎:不带 -f 就被拒绝,带上 -f 才成功(实测行为)。
            var forced = args.Contains(ImagesService.ForceFlag);
            return Task.FromResult(forced
                ? new WslcCliResult(0, "已删除: sha256:aaef90e06523", "")
                : new WslcCliResult(1, "", ConflictStdErr));
        };
        var vm = Create(cli);
        var row = vm.Items[0];
        var imagesBefore = cli.CountCalls("images");

        await vm.ExecuteRemoveAsync(row);                     // 第一次:常规删除被拒
        Assert.True(row.ForcePending);

        await row.RemoveCommand.ExecuteAsync(null);           // 第二次:由行对象自己决定是否强制

        Assert.Equal(new[] { "rmi", "-f", BusyBoxRef }, cli.LastCall("rmi"));
        Assert.False(row.ForcePending);                       // AC11:用完即复位
        Assert.Equal("删除", row.RemoveLabel);
        Assert.Equal("已删除 " + BusyBoxRef, vm.Notice);
        Assert.Equal(imagesBefore + 1, cli.CountCalls("images"));   // AC8:成功后刷新一次
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ForceIsNeverDefault_OnlyWhenExplicitlyAsked()
    {
        var cli = new RoutingWslcCli { ListJson = TwoRows };
        var vm = Create(cli);

        await vm.ExecuteRemoveAsync(vm.Items[1]);   // 未被引用的镜像:默认路径
        Assert.Equal(new[] { "rmi", "docker.1ms.run/library/hello-world:latest" }, cli.LastCall("rmi"));

        await vm.ExecuteRemoveAsync(vm.Items[1], force: true);   // 显式要求才加 -f
        Assert.Equal(new[] { "rmi", "-f", "docker.1ms.run/library/hello-world:latest" }, cli.LastCall("rmi"));
    }

    // ---------- AC12 · 强制状态活过刷新 ----------

    [Fact]
    public async Task ForcePendingSurvivesRefreshWhileImageIsStillInUse()
    {
        var cli = new RoutingWslcCli { ListJson = TwoRows }.AlwaysFor("rmi", 1, "", ConflictStdErr);
        var vm = Create(cli);
        var row = vm.Items[0];

        await vm.ExecuteRemoveAsync(row);
        Assert.True(row.ForcePending);

        await vm.RefreshAsync();

        Assert.Same(row, vm.Items[0]);                 // 同一行对象
        Assert.True(row.ForcePending);                 // 状态没被刷新冲掉
        Assert.Equal("强制删除", row.RemoveLabel);
    }

    [Fact]
    public async Task ForcePendingIsDroppedWhenTheImageIsNoLongerInUse()
    {
        var cli = new RoutingWslcCli { ListJson = TwoRows }.AlwaysFor("rmi", 1, "", ConflictStdErr);
        var vm = Create(cli);
        await vm.ExecuteRemoveAsync(vm.Items[0]);
        Assert.True(vm.Items[0].ForcePending);

        cli.ListJson = BusyBoxRow.Replace("\"Containers\":\"2\"", "\"Containers\":\"0\"") + "\n" + HelloRow;
        await vm.RefreshAsync();                       // 占用消失了

        Assert.False(vm.Items[0].ForcePending);        // 不再保留这个破坏性入口
        Assert.Equal("删除", vm.Items[0].RemoveLabel);
    }

    // ---------- 行对象的数据转发(界面绑定用) ----------

    [Fact]
    public void RowObject_ForwardsImageFields()
    {
        var vm = Create(new RoutingWslcCli { ListJson = TwoRows });
        var row = vm.Items[0];

        Assert.Equal(BusyBoxRef, row.Reference);
        Assert.True(row.IsInUse);
        Assert.Equal(2, row.ContainerCount);
        Assert.Equal("被 2 个容器引用", row.UsageText);
        Assert.Equal("4.45MB", row.SizeText);
        Assert.Contains("ID aaef90e06523", row.SubtleLine);
        Assert.StartsWith("docker.1ms.run/library/busybox:latest", row.DisplayName);

        // 删除按钮走的是行对象自己的判断(AC11)
        row.ForcePending = true;
        Assert.True(row.RemoveCommand.CanExecute(null));
    }

    private static ImagesPageViewModel Create(RoutingWslcCli cli) =>
        new(new ImagesService(cli), uiThread: null);

    /// <summary>行数据换成新对象后,引用仍然稳定(AC12 的前提)。</summary>
    [Fact]
    public void ReferenceStaysStableAcrossParsedCopies()
    {
        var first = wsld.Core.Parsing.WslcJsonParser.ParseImages(TwoRows)[0];
        var second = wsld.Core.Parsing.WslcJsonParser.ParseImages(TwoRows)[0];

        Assert.NotSame(first, second);
        Assert.Equal(first.Reference, second.Reference);
    }
}