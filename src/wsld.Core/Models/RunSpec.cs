using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace wsld.Core.Models;

/// <summary>
/// "一条正在编排的 wslc run"的完整可编辑状态(功能 012)。
/// 这个类本身就是绑定源:每个属性都带变更通知,命令编排页订阅它来实时重拼命令;
/// 拼装、校验、反向解析都不在这里,在 Services/RunCommandBuilder。
/// 属性一律是"留空 = 不输出这个选项"(Detach 例外:默认 true,对应界面默认勾选后台运行)。
/// </summary>
public sealed partial class RunSpec : ObservableObject
{
    // ---- 基本 ----

    /// <summary>镜像引用,唯一必填项。</summary>
    [ObservableProperty] private string _image = "";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _detach = true;
    [ObservableProperty] private bool _rm;
    [ObservableProperty] private bool _publishAll;
    [ObservableProperty] private bool _interactive;
    [ObservableProperty] private bool _tty;

    /// <summary>空串 = 不输出 --pull(wslc 自己的默认就是 missing);否则 always / missing / never。</summary>
    [ObservableProperty] private string _pullPolicy = "";

    // ---- 初始化命令 ----

    [ObservableProperty] private string _entrypoint = "";

    /// <summary>镜像默认入口的覆盖命令(第一个位置参数)。</summary>
    [ObservableProperty] private string _command = "";

    /// <summary>跟在命令后面的参数,原样一段文本:按空白分词,需要带空格时用引号包住。</summary>
    [ObservableProperty] private string _arguments = "";

    // ---- 网络与资源 ----

    [ObservableProperty] private string _hostname = "";
    [ObservableProperty] private string _network = "";
    [ObservableProperty] private string _cpus = "";
    [ObservableProperty] private string _memory = "";
    [ObservableProperty] private string _shmSize = "";

    // ---- 工作环境 ----

    [ObservableProperty] private string _workdir = "";
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _envFile = "";

    // ---- 健康检查 ----

    [ObservableProperty] private string _healthCmd = "";
    [ObservableProperty] private string _healthInterval = "";
    [ObservableProperty] private string _healthTimeout = "";

    /// <summary>整数存成字符串,校验在 builder 里做,界面只管一个文本框。</summary>
    [ObservableProperty] private string _healthRetries = "";

    [ObservableProperty] private string _healthStartPeriod = "";
    [ObservableProperty] private bool _noHealthcheck;

    // ---- 逃生舱 ----

    /// <summary>原样透传的额外参数(按引号感知的规则分词),例如 <c>--gpus all --dns 8.8.8.8</c>。</summary>
    [ObservableProperty] private string _extraArgs = "";

    public ObservableCollection<PortRow> Ports { get; } = new();
    public ObservableCollection<VolumeRow> Volumes { get; } = new();
    public ObservableCollection<EnvRow> EnvVars { get; } = new();
    public ObservableCollection<LabelRow> Labels { get; } = new();
    public ObservableCollection<PathRow> TmpfsPaths { get; } = new();
}

/// <summary>一行端口发布:最终拼成 docker 惯例的 [绑定IP:]主机端口:容器端口[/协议]。</summary>
public sealed partial class PortRow : ObservableObject
{
    [ObservableProperty] private string _hostIp = "";
    [ObservableProperty] private string _hostPort = "";
    [ObservableProperty] private string _containerPort = "";

    /// <summary>tcp 或 udp;空串按 tcp 处理(不输出 /tcp,保持命令干净)。</summary>
    [ObservableProperty] private string _protocol = "tcp";

    /// <summary>协议下拉框的序号:0 = tcp,1 = udp。界面只给这两个选项,wslc 也只认这两个。</summary>
    public int ProtocolIndex
    {
        get => string.Equals(Protocol, "udp", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        set => Protocol = value == 1 ? "udp" : "tcp";
    }

    partial void OnProtocolChanged(string value) => OnPropertyChanged(nameof(ProtocolIndex));

    public bool IsEmpty =>
        HostIp.Trim().Length == 0 &&
        HostPort.Trim().Length == 0 &&
        ContainerPort.Trim().Length == 0;
}

/// <summary>一行挂载:来源 = 主机路径或卷名,目标 = 容器内绝对路径。</summary>
public sealed partial class VolumeRow : ObservableObject
{
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private bool _readOnly;

    public bool IsEmpty => Source.Trim().Length == 0 && Target.Trim().Length == 0;
}

/// <summary>一行环境变量,拼成 -e key=value(value 为空时只输出 key=)。</summary>
public sealed partial class EnvRow : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";

    public bool IsEmpty => Key.Trim().Length == 0 && Value.Length == 0;
}

/// <summary>一行标签,拼成 -l key=value。</summary>
public sealed partial class LabelRow : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";

    public bool IsEmpty => Key.Trim().Length == 0 && Value.Length == 0;
}

/// <summary>单个文本行(tmpfs 路径这类)。</summary>
public sealed partial class PathRow : ObservableObject
{
    [ObservableProperty] private string _path = "";

    public bool IsEmpty => Path.Trim().Length == 0;
}