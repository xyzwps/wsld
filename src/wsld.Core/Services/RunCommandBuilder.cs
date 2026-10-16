using System.Globalization;
using System.Text;
using wsld.Core.Models;

namespace wsld.Core.Services;

/// <summary>一条校验问题:Code 决定取哪句文案,Row 是所在行的行号(1 基),Value 是回显用的原始输入。</summary>
public sealed record RunIssue(RunIssueCode Code, int? Row = null, string? Value = null);

public enum RunIssueCode
{
    ImageMissing,
    PortContainerMissing,
    PortNumber,
    PortRange,
    PortProtocol,
    VolumeSource,
    VolumeTarget,
    TmpfsPath,
    EnvKey,
    LabelKey,
    HealthRetries,
    Cpus,
}

/// <summary>
/// wslc run 命令的"纯函数三件套"(功能 012):
/// Build 把 RunSpec 拼成进程参数数组,Display 把它拼成可以贴进终端的一条命令,
/// Validate 找出表单里还不对的地方,Parse 再把一条现成的 wslc run 读回 RunSpec。
/// 这里不碰 i18n、不碰 UI、不碰进程 —— 全部可以离线单测。
/// </summary>
public static class RunCommandBuilder
{
    // ---- 拼装 ----

    /// <summary>
    /// 生成 <c>IWslcCli.RunAsync</c> 直接可用的参数数组(首个元素是动词 run)。
    /// 输出顺序固定,和 Display 完全一致:标志 → 选项 → 端口/挂载 → 位置参数,
    /// 界面上看到的和真正执行的内容同源,不存在"预览 ≠ 执行"的缝。
    /// </summary>
    public static IReadOnlyList<string> Build(RunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var args = new List<string> { "run" };

        if (spec.Detach) args.Add("-d");
        if (spec.Interactive) args.Add("-i");
        if (spec.Tty) args.Add("-t");
        if (spec.Rm) args.Add("--rm");
        if (spec.PublishAll) args.Add("-P");

        AddValue(args, "--pull", spec.PullPolicy);
        AddValue(args, "--name", spec.Name);
        AddValue(args, "-h", spec.Hostname);
        AddValue(args, "--network", spec.Network);
        AddValue(args, "--cpus", spec.Cpus);
        AddValue(args, "-m", spec.Memory);
        AddValue(args, "--shm-size", spec.ShmSize);
        AddValue(args, "-w", spec.Workdir);
        AddValue(args, "-u", spec.User);

        foreach (var env in spec.EnvVars)
        {
            if (env.IsEmpty) continue;
            var key = env.Key.Trim();
            args.Add("-e");
            args.Add(env.Value.Length == 0 ? key + "=" : key + "=" + env.Value);
        }

        AddValue(args, "--env-file", spec.EnvFile);

        foreach (var label in spec.Labels)
        {
            if (label.IsEmpty) continue;
            args.Add("-l");
            args.Add(label.Key.Trim() + "=" + label.Value);
        }

        foreach (var port in spec.Ports)
        {
            if (port.IsEmpty) continue;
            args.Add("-p");
            args.Add(BuildPortSpec(port));
        }

        AddValue(args, "--entrypoint", spec.Entrypoint);

        foreach (var tmpfs in spec.TmpfsPaths)
        {
            if (tmpfs.IsEmpty) continue;
            args.Add("--tmpfs");
            args.Add(tmpfs.Path.Trim());
        }

        foreach (var volume in spec.Volumes)
        {
            if (volume.IsEmpty) continue;
            args.Add("-v");
            args.Add(BuildVolumeSpec(volume));
        }

        AddValue(args, "--health-cmd", spec.HealthCmd);
        AddValue(args, "--health-interval", spec.HealthInterval);
        AddValue(args, "--health-timeout", spec.HealthTimeout);
        AddValue(args, "--health-retries", spec.HealthRetries);
        AddValue(args, "--health-start-period", spec.HealthStartPeriod);
        if (spec.NoHealthcheck) args.Add("--no-healthcheck");

        foreach (var extra in Tokenize(spec.ExtraArgs)) args.Add(extra);

        var image = spec.Image.Trim();
        if (image.Length > 0) args.Add(image);

        var command = spec.Command.Trim();
        if (command.Length > 0) args.Add(command);

        foreach (var argument in Tokenize(spec.Arguments)) args.Add(argument);
        return args;
    }

    /// <summary>拼成给人复制的一条命令(双引号 + CommandLineToArgvW 转义,PowerShell 与 cmd 都能直接跑)。</summary>
    public static string Display(RunSpec spec) =>
        "wslc " + string.Join(' ', Build(spec).Select(Quote));

    private static void AddValue(List<string> args, string flag, string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length > 0)
        {
            args.Add(flag);
            args.Add(trimmed);
        }
    }

    private static string BuildPortSpec(PortRow port)
    {
        var ip = port.HostIp.Trim();
        var host = port.HostPort.Trim();
        var container = port.ContainerPort.Trim();
        var protocol = port.Protocol.Trim().ToLowerInvariant();

        var sb = new StringBuilder();
        if (ip.Length > 0) sb.Append(ip).Append(':');
        if (host.Length > 0 || ip.Length > 0) sb.Append(host).Append(':');
        sb.Append(container);
        if (protocol.Length > 0 && protocol != "tcp") sb.Append('/').Append(protocol);
        return sb.ToString();
    }

    private static string BuildVolumeSpec(VolumeRow volume) =>
        volume.Source.Trim() + ":" + volume.Target.Trim() + (volume.ReadOnly ? ":ro" : "");

    // ---- 校验 ----

    /// <summary>收集所有问题(不只第一个),界面逐行列出;空列表 = 可以执行。</summary>
    public static IReadOnlyList<RunIssue> Validate(RunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var issues = new List<RunIssue>();

        if (spec.Image.Trim().Length == 0) issues.Add(new RunIssue(RunIssueCode.ImageMissing));

        var rowIndex = 0;
        foreach (var port in spec.Ports)
        {
            rowIndex++;
            if (port.IsEmpty) continue;

            var container = port.ContainerPort.Trim();
            int cLo = 0, cHi = 0;
            var containerValid = false;
            if (container.Length == 0)
            {
                issues.Add(new RunIssue(RunIssueCode.PortContainerMissing, rowIndex));
            }
            else if ((containerValid = TryPortOrRange(container, out cLo, out cHi)) == false)
            {
                issues.Add(new RunIssue(RunIssueCode.PortNumber, rowIndex, container));
            }

            var host = port.HostPort.Trim();
            if (host.Length > 0)
            {
                if (!TryPortOrRange(host, out var hLo, out var hHi))
                {
                    issues.Add(new RunIssue(RunIssueCode.PortNumber, rowIndex, host));
                }
                else if (containerValid && hHi - hLo != cHi - cLo)
                {
                    // 一端写范围另一端写单端口(或长度不同的两个范围),wslc 会直接拒
                    issues.Add(new RunIssue(RunIssueCode.PortRange, rowIndex));
                }
            }

            var protocol = port.Protocol.Trim();
            if (protocol.Length > 0 &&
                !protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase) &&
                !protocol.Equals("udp", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new RunIssue(RunIssueCode.PortProtocol, rowIndex));
            }
        }

        rowIndex = 0;
        foreach (var volume in spec.Volumes)
        {
            rowIndex++;
            if (volume.IsEmpty) continue;
            if (volume.Source.Trim().Length == 0)
                issues.Add(new RunIssue(RunIssueCode.VolumeSource, rowIndex));
            if (!StartsContainerPath(volume.Target))
                issues.Add(new RunIssue(RunIssueCode.VolumeTarget, rowIndex));
        }

        rowIndex = 0;
        foreach (var tmpfs in spec.TmpfsPaths)
        {
            rowIndex++;
            if (tmpfs.IsEmpty) continue;
            if (!StartsContainerPath(tmpfs.Path))
                issues.Add(new RunIssue(RunIssueCode.TmpfsPath, rowIndex));
        }

        rowIndex = 0;
        foreach (var env in spec.EnvVars)
        {
            rowIndex++;
            if (env.IsEmpty) continue;
            var key = env.Key.Trim();
            if (key.Length == 0 || key.Contains('='))
                issues.Add(new RunIssue(RunIssueCode.EnvKey, rowIndex));
        }

        rowIndex = 0;
        foreach (var label in spec.Labels)
        {
            rowIndex++;
            if (label.IsEmpty) continue;
            var key = label.Key.Trim();
            if (key.Length == 0 || key.Contains('='))
                issues.Add(new RunIssue(RunIssueCode.LabelKey, rowIndex));
        }

        if (spec.HealthRetries.Trim().Length > 0 &&
            (!int.TryParse(spec.HealthRetries.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var retries) || retries < 0))
        {
            issues.Add(new RunIssue(RunIssueCode.HealthRetries));
        }

        if (spec.Cpus.Trim().Length > 0 &&
            (!double.TryParse(spec.Cpus.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cpus) || cpus <= 0))
        {
            issues.Add(new RunIssue(RunIssueCode.Cpus));
        }

        return issues;
    }

    /// <summary>端口可以写单个(1..65535)也可以写范围(start..end,start&lt;=end);跨度供范围配对检查。</summary>
    private static bool TryPortOrRange(string text, out int low, out int high)
    {
        low = high = 0;
        if (!text.Contains('-'))
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var single) ||
                single < 1 || single > 65535) return false;
            low = high = single;
            return true;
        }

        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out low) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out high)) return false;
        return low >= 1 && high <= 65535 && low <= high;
    }

    private static bool StartsContainerPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && (value[0] == '/' || value[0] == '~');

    // ---- 反向解析(粘贴已有命令) ----

    /// <summary>wslc run 文档里"带值"的选项全集;未知选项只透传标志本身,不会误吞后面的镜像名。</summary>
    private static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal)
    {
        "--cidfile", "--cpus", "--dns", "--dns-option", "--dns-search", "--domainname",
        "--entrypoint", "-e", "--env", "--env-file", "--gpus",
        "--health-cmd", "--health-interval", "--health-retries", "--health-start-period", "--health-timeout",
        "-h", "--hostname", "--ip", "-l", "--label", "-m", "--memory",
        "--mount", "--name", "--network", "--network-alias",
        "-p", "--publish", "--pull", "--shm-size", "--stop-signal", "--stop-timeout",
        "--tmpfs", "--ulimit", "-u", "--user", "-v", "--volume", "-w", "--workdir",
    };

    /// <summary>--pull 的三个显式取值;空串 = 不下发这个选项(界面上的"默认")。</summary>
    public static IReadOnlyList<string> PullPolicies { get; } = new[] { "always", "missing", "never" };

    /// <summary>读回一条 <c>wslc run ...</c> 命令(允许开头的 wslc / 带路径的 wslc.exe)。</summary>
    public static bool TryParse(string? commandLine, out RunSpec? spec)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(commandLine)) return false;

        var tokens = Tokenize(commandLine);
        var start = 0;

        if (tokens.Count > 0 && IsWslcToken(tokens[0])) start = 1;
        if (start >= tokens.Count) return false;

        if (tokens[start].Equals("container", StringComparison.OrdinalIgnoreCase) &&
            start + 1 < tokens.Count &&
            tokens[start + 1].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            start += 2;
        }
        else if (tokens[start].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            start += 1;
        }
        else
        {
            return false;
        }

        var result = new RunSpec { Detach = false };
        var positionals = new List<string>();

        for (var i = start; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // 位置参数开始后,后面的一切都归位置参数(镜像 / 命令 / 容器参数)。
            // 这是 docker 类 CLI 的通用约定:选项必须写在镜像之前。
            if (positionals.Count > 0 || !token.StartsWith('-') || token == "-")
            {
                positionals.Add(token);
                continue;
            }

            string? TakeValue() => i + 1 < tokens.Count ? tokens[++i] : null;

            switch (token)
            {
                case "-d":
                case "--detach": result.Detach = true; break;
                case "-i":
                case "--interactive": result.Interactive = true; break;
                case "-t":
                case "--tty": result.Tty = true; break;
                case "--rm": result.Rm = true; break;
                case "-P":
                case "--publish-all": result.PublishAll = true; break;
                case "--no-healthcheck": result.NoHealthcheck = true; break;

                case "--pull":
                    // 认得的三种策略落回下拉框;认不得的(--pull if-not-present 这类)整对透传到"其他参数",
                    // 否则下拉框显示"默认"而命令里跑的是别的,预览和执行就不一致了。
                    var pull = TakeValue();
                    if (pull is null) break;
                    if (PullPolicies.Contains(pull)) result.PullPolicy = pull;
                    else result.ExtraArgs = JoinQuoted(result.ExtraArgs, token, pull);
                    break;
                case "--name": result.Name = TakeValue() ?? ""; break;
                case "-h":
                case "--hostname": result.Hostname = TakeValue() ?? ""; break;
                case "--network": result.Network = TakeValue() ?? ""; break;
                case "--cpus": result.Cpus = TakeValue() ?? ""; break;
                case "-m":
                case "--memory": result.Memory = TakeValue() ?? ""; break;
                case "--shm-size": result.ShmSize = TakeValue() ?? ""; break;
                case "-w":
                case "--workdir": result.Workdir = TakeValue() ?? ""; break;
                case "-u":
                case "--user": result.User = TakeValue() ?? ""; break;
                case "--env-file": result.EnvFile = TakeValue() ?? ""; break;
                case "--entrypoint": result.Entrypoint = TakeValue() ?? ""; break;
                case "--health-cmd": result.HealthCmd = TakeValue() ?? ""; break;
                case "--health-interval": result.HealthInterval = TakeValue() ?? ""; break;
                case "--health-timeout": result.HealthTimeout = TakeValue() ?? ""; break;
                case "--health-retries": result.HealthRetries = TakeValue() ?? ""; break;
                case "--health-start-period": result.HealthStartPeriod = TakeValue() ?? ""; break;

                case "-e":
                case "--env":
                    var env = TakeValue();
                    if (env is not null) result.EnvVars.Add(ParseEnv(env));
                    break;
                case "-l":
                case "--label":
                    var label = TakeValue();
                    if (label is not null) result.Labels.Add(ParseLabel(label));
                    break;
                case "-p":
                case "--publish":
                    var port = TakeValue();
                    if (port is not null) result.Ports.Add(ParsePortSpec(port));
                    break;
                case "-v":
                case "--volume":
                    var volume = TakeValue();
                    if (volume is not null) result.Volumes.Add(ParseVolumeSpec(volume));
                    break;
                case "--tmpfs":
                    var path = TakeValue();
                    if (path is not null) result.TmpfsPaths.Add(new PathRow { Path = path });
                    break;

                default:
                    if (ValueFlags.Contains(token) && i + 1 < tokens.Count)
                    {
                        // 已知带值但表单没有对应控件(--gpus/--dns/--ulimit…):整对透传到"其他参数"
                        result.ExtraArgs = JoinQuoted(result.ExtraArgs, token, tokens[++i]);
                    }
                    else
                    {
                        result.ExtraArgs = JoinQuoted(result.ExtraArgs, token);
                    }
                    break;
            }
        }

        if (positionals.Count > 0) result.Image = positionals[0];
        if (positionals.Count > 1) result.Command = positionals[1];
        if (positionals.Count > 2) result.Arguments = string.Join(' ', positionals.Skip(2).Select(Quote));

        spec = result;
        return true;
    }

    private static bool IsWslcToken(string token)
    {
        var name = token.Trim('"', '\'').Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Equals("wslc", StringComparison.OrdinalIgnoreCase);
    }

    private static string JoinQuoted(string current, params string[] parts)
    {
        var joined = string.Join(' ', parts.Select(Quote));
        return current.Trim().Length == 0 ? joined : current.TrimEnd() + " " + joined;
    }

    private static EnvRow ParseEnv(string token)
    {
        var row = new EnvRow();
        var eq = token.IndexOf('=');
        if (eq < 0) row.Key = token;
        else
        {
            row.Key = token[..eq];
            row.Value = token[(eq + 1)..];
        }
        return row;
    }

    private static LabelRow ParseLabel(string token)
    {
        var row = new LabelRow();
        var eq = token.IndexOf('=');
        if (eq < 0) row.Key = token;
        else
        {
            row.Key = token[..eq];
            row.Value = token[(eq + 1)..];
        }
        return row;
    }

    private static PortRow ParsePortSpec(string spec)
    {
        // 界面上协议只有 tcp / udp 两个选项,读回来时一并归到这两种:写 sctp 之类的 wslc 本来也不认,
        // 归成 tcp 之后表单、生成的命令、校验三者说的是同一句话。
        var row = new PortRow { Protocol = "tcp" };
        var body = spec;
        var slash = body.LastIndexOf('/');
        if (slash >= 0)
        {
            row.Protocol = body[(slash + 1)..].ToLowerInvariant() == "udp" ? "udp" : "tcp";
            body = body[..slash];
        }

        var parts = body.Split(':');
        switch (parts.Length)
        {
            case 1:
                row.ContainerPort = parts[0];
                break;
            case 2:
                row.HostPort = parts[0];
                row.ContainerPort = parts[1];
                break;
            default:
                row.HostIp = parts[0];
                row.HostPort = parts[1];
                row.ContainerPort = string.Join(':', parts.Skip(2));
                break;
        }
        return row;
    }

    private static VolumeRow ParseVolumeSpec(string spec)
    {
        var row = new VolumeRow();
        // Windows 盘符里的冒号(C:\data)不能当分隔符:index 1 处、且后面紧跟反斜杠的冒号属于盘符
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < spec.Length; i++)
        {
            var c = spec[i];
            var isDriveColon = i == 1 && char.IsLetter(spec[0]) && spec.Length > 2 && spec[2] == '\\';
            if (c == ':' && !isDriveColon)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        parts.Add(current.ToString());

        if (parts.Count >= 1) row.Source = parts[0];
        if (parts.Count >= 2) row.Target = parts[1];
        if (parts.Count >= 3) row.ReadOnly = parts[2].Equals("ro", StringComparison.OrdinalIgnoreCase);
        return row;
    }

    // ---- 分词与转义 ----

    /// <summary>
    /// 引号感知的命令行分词,双引号内部按 CommandLineToArgvW 语义解反斜杠:
    /// 2n 个反斜杠 + 引号 → n 个反斜杠、引号本身定界;2n+1 个 → n 个反斜杠 + 一个字面引号。
    /// 单引号段(PowerShell 手写命令常见)整段原样。这是转义与还原的"逆函数"对,
    /// 保证 Display → 复制 → 粘贴回来 → Parse 是幂等的(测试里有往返断言)。
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var current = new StringBuilder();
        var hasToken = false;
        var inQuotes = false;
        var i = 0;

        void Flush()
        {
            if (hasToken)
            {
                tokens.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (!inQuotes && (c == ' ' || c == '\t' || c == '\r' || c == '\n'))
            {
                Flush();
                i++;
                continue;
            }

            if (c == '\\')
            {
                var backslashes = 0;
                var j = i;
                while (j < text.Length && text[j] == '\\') { backslashes++; j++; }

                hasToken = true;
                if (j < text.Length && text[j] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"'); // 奇数:最后那个反斜杠"护送"引号,它是字面量
                    }
                    else
                    {
                        inQuotes = !inQuotes; // 偶数:这对引号是定界符
                    }
                    i = j + 1;
                    continue;
                }

                current.Append('\\', backslashes); // 后面没有引号,反斜杠原样
                i = j;
                continue;
            }

            if (c == '"')
            {
                hasToken = true;
                inQuotes = !inQuotes;
                i++;
                continue;
            }

            if (c == '\'' && !inQuotes)
            {
                // 单引号段:整段原样(我们从不生成它,只为读回 PowerShell 手写命令时不炸)
                hasToken = true;
                i++;
                while (i < text.Length && text[i] != '\'')
                {
                    current.Append(text[i]);
                    i++;
                }
                i++;
                continue;
            }

            hasToken = true;
            current.Append(c);
            i++;
        }

        Flush();
        return tokens;
    }

    /// <summary>
    /// 需要时才加双引号,并按 CommandLineToArgvW 规则转义(" 前与结尾的反斜杠加倍)。
    /// 空格之外还要罩住 PowerShell/cmd 的控制字符(; &amp; | &lt; &gt; ( ) ^ $ ` 与单引号),
    /// 这样同一行复制品在两种终端里都站得住。
    /// </summary>
    public static string Quote(string token)
    {
        if (token.Length > 0 && !NeedsQuotes(token)) return token;

        var sb = new StringBuilder(token.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in token)
        {
            if (c == '\\')
            {
                backslashes++;
                sb.Append(c);
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes); // 让刚才那段反斜杠也翻倍
                backslashes = 0;
                sb.Append("\\\"");
                continue;
            }

            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes); // 结尾反斜杠加倍,免得吃掉收尾引号
        sb.Append('"');
        return sb.ToString();
    }

    private static bool NeedsQuotes(string token)
    {
        foreach (var c in token)
        {
            if (c is ' ' or '\t' or '"' or '\'' or ';' or '&' or '|' or '<' or '>' or '(' or ')' or '^' or '$' or '`')
                return true;
        }
        return false;
    }
}