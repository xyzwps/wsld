using wsld.Core.Models;
using wsld.Core.Services;
using wsld.Core.ViewModels;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>功能 012:"命令编排"页 ViewModel —— 实时重拼、校验门禁、就地执行与粘贴读回。</summary>
public class RunBuilderPageViewModelTests
{
    private static (RunBuilderPageViewModel vm, RoutingWslcCli cli) NewPage()
    {
        var cli = new RoutingWslcCli();
        var vm = new RunBuilderPageViewModel(new ContainersService(cli), new ImagesService(cli), uiThread: null);
        return (vm, cli);
    }

    [Fact]
    public void FreshPage_PreviewsDetachedRunAndNagsForImageOnly()
    {
        var (vm, _) = NewPage();

        Assert.Equal("wslc run -d", vm.GeneratedCommand);
        Assert.True(vm.HasValidationIssues);
        Assert.Contains("镜像不能为空", vm.ValidationText);
        Assert.False(vm.CanExecute);
        Assert.False(vm.CanCopy);
    }

    [Fact]
    public void TypingImage_RebuildsPreviewAndUnlocksExecute()
    {
        var (vm, _) = NewPage();

        vm.Spec.Image = "hello-world";

        Assert.Equal("wslc run -d hello-world", vm.GeneratedCommand);
        Assert.False(vm.HasValidationIssues);
        Assert.True(vm.CanExecute);
        Assert.True(vm.CanCopy);
    }

    [Fact]
    public void WorkdirAndUser_FlowIntoTheGeneratedCommand()
    {
        var (vm, _) = NewPage();
        vm.Spec.Image = "nginx";

        vm.Spec.Workdir = "/srv";
        vm.Spec.User = "1000:1000";

        Assert.Contains("-w /srv -u 1000:1000", vm.GeneratedCommand);
    }

    [Fact]
    public void IncompletePortRow_NamesItsRowNumberAndBlocksExecute()
    {
        var (vm, _) = NewPage();
        vm.Spec.Image = "nginx";

        vm.AddPortCommand.Execute(null);
        vm.Spec.Ports[0].HostPort = "8080";

        Assert.True(vm.HasValidationIssues);
        Assert.Contains("端口第 1 行", vm.ValidationText);
        Assert.False(vm.CanExecute);

        vm.Spec.Ports[0].ContainerPort = "80";
        Assert.False(vm.HasValidationIssues);
        Assert.Equal("wslc run -d -p 8080:80 nginx", vm.GeneratedCommand);

        vm.Spec.Ports.RemoveAt(0);
        Assert.False(vm.HasValidationIssues);
        Assert.Equal("wslc run -d nginx", vm.GeneratedCommand);
    }

    [Fact]
    public void PullPolicyDropdown_MapsIndexToFlagBothWays()
    {
        var (vm, _) = NewPage();
        vm.Spec.Image = "nginx";

        Assert.Equal(0, vm.PullPolicyIndex);

        vm.PullPolicyIndex = 2;
        Assert.Equal("missing", vm.Spec.PullPolicy);
        Assert.Contains("--pull missing", vm.GeneratedCommand);

        vm.Spec.PullPolicy = "never";
        Assert.Equal(3, vm.PullPolicyIndex);

        vm.PullPolicyIndex = 0;
        Assert.Equal("", vm.Spec.PullPolicy);
        Assert.DoesNotContain("--pull", vm.GeneratedCommand);
    }

    [Fact]
    public void Import_EmptyClipboard_TellsYouNothingWasThere()
    {
        var (vm, _) = NewPage();

        vm.ImportCommandText("   ");

        Assert.True(vm.HasNotice);
        Assert.Equal("剪贴板里没有文本,先复制一条 wslc run 命令再来。", vm.Notice);
    }

    [Fact]
    public void Import_NotARunCommand_ReportsError()
    {
        var (vm, _) = NewPage();

        vm.ImportCommandText("kubectl run nginx --image=nginx");

        Assert.True(vm.HasError);
        Assert.Contains("这不是 wslc run 命令", vm.Error);
    }

    [Fact]
    public void Import_FullRunCommand_FillsTheFormAndRebuilds()
    {
        var (vm, _) = NewPage();

        vm.ImportCommandText(
            "wslc run -d --name web -p 8080:80/udp -v C:\\data:/app:ro -e MODE=fast nginx sleep infinity");

        Assert.Equal("nginx", vm.Spec.Image);
        Assert.Equal("web", vm.Spec.Name);
        Assert.Equal("sleep", vm.Spec.Command);
        Assert.Equal("infinity", vm.Spec.Arguments);
        Assert.Equal("udp", Assert.Single(vm.Spec.Ports).Protocol);
        Assert.Equal(@"C:\data", Assert.Single(vm.Spec.Volumes).Source);
        Assert.Equal("fast", Assert.Single(vm.Spec.EnvVars).Value);
        Assert.False(vm.HasValidationIssues);
        Assert.Contains("已从剪贴板读回命令", vm.Notice);
    }

    [Fact]
    public async Task Execute_Success_ReportsContainerIdAndOutput()
    {
        var (vm, cli) = NewPage();
        cli.EnqueueStream(0, "abc123\n");
        vm.Spec.Image = "hello-world";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(RunCommandBuilder.Build(vm.Spec), cli.LastStreamCall("run"));
        Assert.False(vm.HasError);
        Assert.True(vm.HasNotice);
        Assert.Contains("已运行容器 abc123", vm.Notice);
        Assert.Contains("abc123", vm.OutputText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Execute_NonZeroExit_SurfacesStderrAsError()
    {
        var (vm, cli) = NewPage();
        cli.EnqueueStream(1, "", "镜像不存在: nosuch\n如果此错误是意外错误,请提交 issue。");
        vm.Spec.Image = "nosuch";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("镜像不存在", vm.Error);
        Assert.DoesNotContain("如果此错误是意外错误", vm.Error);
        Assert.False(vm.HasNotice);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Execute_Cancelled_StopsOnlyTheWslcProcessAndSaysSo()
    {
        var (vm, cli) = NewPage();
        cli.StreamHandler = (_, _, ct) =>
        {
            var tcs = new TaskCompletionSource<WslcCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        };
        vm.Spec.Image = "ubuntu";
        vm.Spec.Detach = false;

        var running = vm.ExecuteCommand.ExecuteAsync(null);
        while (!vm.IsBusy) await Task.Yield();
        vm.CancelCommand.Execute(null);
        await running;

        Assert.True(vm.HasNotice);
        Assert.Contains("已终止 wslc 进程", vm.Notice);
        Assert.False(vm.HasError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void CopyOutcome_LandsAsNoticeOrError()
    {
        var (vm, _) = NewPage();

        vm.MarkCopied();
        Assert.True(vm.HasNotice);
        Assert.Equal("命令已复制到剪贴板。", vm.Notice);

        vm.MarkCopyFailed();
        Assert.True(vm.HasError);
        Assert.Contains("复制到剪贴板失败", vm.Error);
    }

    [Fact]
    public void ClearForm_ResetsEverythingBackToInitialState()
    {
        var (vm, _) = NewPage();
        vm.Spec.Image = "nginx";
        vm.Spec.Name = "web";
        vm.AddPortCommand.Execute(null);
        vm.Spec.Ports[0].HostPort = "8080";
        vm.Spec.Ports[0].ContainerPort = "80";
        vm.MarkCopyFailed();

        vm.ClearFormCommand.Execute(null);

        Assert.Equal("wslc run -d", vm.GeneratedCommand);
        Assert.Equal("", vm.Spec.Image);
        Assert.Empty(vm.Spec.Ports);
        Assert.False(vm.HasError);
        Assert.False(vm.HasNotice);
        Assert.Equal("", vm.OutputText);
    }

    [Fact]
    public async Task Execute_RecordsHistoryNewestFirst_AndSkipsConsecutiveDuplicates()
    {
        var (vm, cli) = NewPage();
        cli.EnqueueStream(0, "abc123\n");
        vm.Spec.Image = "hello-world";

        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Single(vm.History.Entries);

        vm.Spec.Image = "nginx";
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.History.Entries.Count);
        Assert.Equal("wslc run -d nginx", vm.History.Entries[0].Command);
        Assert.True(vm.HasHistory);
    }

    [Fact]
    public void RestoreFromHistory_FillsTheFormAndAnnouncesIt()
    {
        var history = new RunHistory();
        var cli = new RoutingWslcCli();
        var vm = new RunBuilderPageViewModel(new ContainersService(cli), new ImagesService(cli), uiThread: null, history);
        history.Record("wslc run -d --name web -p 8080:80/udp nginx");

        vm.RestoreFromHistory(history.Entries[0]);

        Assert.Equal("nginx", vm.Spec.Image);
        Assert.Equal("web", vm.Spec.Name);
        Assert.Equal("udp", Assert.Single(vm.Spec.Ports).Protocol);
        Assert.False(vm.HasValidationIssues);
        Assert.Contains("已从执行历史读回命令", vm.Notice);
    }

    [Fact]
    public void RunHistory_KeepsAtMostCapacityEntries()
    {
        var history = new RunHistory();

        for (var i = 0; i < RunHistory.Capacity + 5; i++)
        {
            history.Record($"wslc run -d img{i}");
        }

        Assert.Equal(RunHistory.Capacity, history.Entries.Count);
        Assert.Equal($"wslc run -d img{RunHistory.Capacity + 4}", history.Entries[0].Command);
    }

    [Fact]
    public async Task RefreshImages_PopulatesTaggedReferencesOnly_AndSurvivesFailure()
    {
        var (vm, cli) = NewPage();
        cli.ListJson = "";
        await vm.RefreshImagesAsync();
        Assert.Empty(vm.ImageReferences);

        var cli2 = new RoutingWslcCli
        {
            ListJson = wsld.Tests.FixtureLoader.Read("wslc-images.ndjson"),
        };
        var vm2 = new RunBuilderPageViewModel(new ContainersService(cli2), new ImagesService(cli2), uiThread: null);
        await vm2.RefreshImagesAsync();
        Assert.NotEmpty(vm2.ImageReferences);
    }
}