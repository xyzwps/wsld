using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace wsld.Core.WslcCli;

/// <summary>
/// 一个最小的 Windows 伪终端(ConPTY)会话(功能 011)。
/// 把子进程挂到伪控制台后,子进程认为自己面对真实终端,进度会**边产生边写出来**;
/// 我们从管道这一侧读它渲染到"屏幕"上的字节流。实测对比:
/// 普通重定向管道下 <c>wslc pull</c> 要到进程退出才一次性冲刷 45 行,挂进 ConPTY 后
/// 每 ~20 秒就能看到一行 "Downloading 1.073MB" 这样的实时刷新。
/// 创建失败(理论上只可能出现在 1809 以前的系统)时 <see cref="TryStart"/> 返回 false,
/// 调用方退回普通管道路径即可。
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    // GetStdHandle/SetStdHandle 用的索引,对应 Win32 的 STD_INPUT_HANDLE/STD_OUTPUT_HANDLE/STD_ERROR_HANDLE。
    private const int StdInput = -10, StdOutput = -11, StdError = -12;
    private static readonly IntPtr InvalidHandle = new(-1);

    private IntPtr _pseudoConsole = IntPtr.Zero;
    private IntPtr _inputRead, _inputWrite, _outputRead, _outputWrite;
    private IntPtr _processHandle = IntPtr.Zero;
    private IntPtr _attributeList = IntPtr.Zero;
    private int _processId; // 在 StartInternal 里才拿到 PID,所以不能是 readonly
    private bool _disposed;

    /// <summary>子进程"屏幕输出"的读端,UTF-8 字节流(含 ANSI 控制序列)。</summary>
    public Stream Output { get; private set; } = null!;

    /// <summary>子进程 PID;取消时按整树终止。</summary>
    public int ProcessId => _processId;

    /// <summary>
    /// 在伪终端里启动 <paramref name="exePath"/>;失败时返回 false 并给出原因,已分配的资源自行回收。
    /// </summary>
    public static bool TryStart(string exePath, IReadOnlyList<string> arguments, int width, int height,
        out ConPtySession? session, out string error)
    {
        session = null;
        error = "";
        var candidate = new ConPtySession();
        try
        {
            if (!candidate.StartInternal(exePath, arguments, width, height, out error))
            {
                candidate.Dispose();
                return false;
            }

            session = candidate;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            candidate.Dispose();
            return false;
        }
    }

    private bool StartInternal(string exePath, IReadOnlyList<string> arguments, int width, int height, out string error)
    {
        error = "";
        if (!CreatePipe(out _inputRead, out _inputWrite, IntPtr.Zero, 0))
        {
            error = "CreatePipe(input) " + Marshal.GetLastWin32Error();
            return false;
        }

        if (!CreatePipe(out _outputRead, out _outputWrite, IntPtr.Zero, 0))
        {
            error = "CreatePipe(output) " + Marshal.GetLastWin32Error();
            return false;
        }

        var size = new Coord { X = (short)width, Y = (short)height };
        // hInput=读端(我们写进 stdin 的数据从这里被控制台读走),hOutput=写端(控制台把屏幕输出写进来)。
        if (CreatePseudoConsole(size, _inputRead, _outputWrite, 0, out _pseudoConsole) != 0)
        {
            error = "CreatePseudoConsole " + Marshal.GetLastWin32Error();
            return false;
        }

        IntPtr listSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        _attributeList = Marshal.AllocHGlobal((int)listSize);
        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref listSize))
        {
            error = "InitAttrList " + Marshal.GetLastWin32Error();
            return false;
        }

        // 注意:cbSize 是"按值"传 SIZE_T,写成 ref 会报 ERROR_BAD_LENGTH(24),踩过这个坑。
        if (!UpdateProcThreadAttribute(_attributeList, 0,
                (IntPtr)ProcThreadAttributePseudoConsole, _pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            error = "UpdateAttrList " + Marshal.GetLastWin32Error();
            return false;
        }

        var startupInfo = new StartupInfoEx();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        startupInfo.lpAttributeList = _attributeList;

        var commandLine = Quote(exePath) + " " + string.Join(' ', arguments.Select(Quote));

        // 大坑(实测,详见 docs/dev-notes.md):子进程的标准句柄默认继承"本进程当前的标准句柄",
        // 而这份继承来的句柄优先级高于伪控制台。父进程的标准输出是管道(dotnet test、被重定向的宿主)
        // 或真实控制台(在终端里跑)时,子进程根本没挂到 ConPTY 上,
        // 于是 wslc 又退化成"进程退出才一次性冲刷",伪终端一帧都读不到 —— 用户看到的就是"只转圈、没进度"。
        // 对策:CreateProcess 前把三个标准句柄临时置空,调用一返回立刻还原。
        // 风险可控:窗口只包住一次 CreateProcess;还原放在 finally 里,异常路径也会执行;
        // 本项目其他进程启动都走 System.Diagnostics.Process + 重定向(自带 STARTF_USESTDHANDLES),不受影响。
        var savedStdHandles = new (int Which, IntPtr Handle)[]
        {
            (StdInput, GetStdHandle(StdInput)),
            (StdOutput, GetStdHandle(StdOutput)),
            (StdError, GetStdHandle(StdError)),
        };
        var cleared = false;
        foreach (var (which, handle) in savedStdHandles)
        {
            if (handle == IntPtr.Zero || handle == InvalidHandle) continue;
            SetStdHandle(which, IntPtr.Zero);
            cleared = true;
        }

        bool created;
        int createError;
        ProcessInformation processInfo;
        try
        {
            created = CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, null, ref startupInfo, out processInfo);
            createError = Marshal.GetLastWin32Error();
        }
        finally
        {
            if (cleared)
            {
                foreach (var (which, handle) in savedStdHandles) SetStdHandle(which, handle);
            }
        }

        if (!created)
        {
            error = "CreateProcess " + createError;
            return false;
        }

        _processHandle = processInfo.hProcess;
        _processId = processInfo.dwProcessId;
        if (processInfo.hThread != IntPtr.Zero) CloseHandle(processInfo.hThread);

        // 读端交给 FileStream:ownsHandle 必须是 true,否则这个管道句柄没人关,每拉一次泄漏一个。
        Output = new FileStream(new SafeFileHandle(_outputRead, ownsHandle: true), FileAccess.Read);
        _outputRead = IntPtr.Zero;

        // 伪控制台在 CreatePseudoConsole 里复制了它拿到的两个管道句柄,我们自己的副本必须当场关掉。
        // 关键是输出**写端**:只要我们的进程还持有它,子进程退出后这根管道就没有"全部写端关闭"的时刻,
        // 读端永远等不到 EOF(探针实测:日志停在最后一帧,读取循环再没返回)。
        Close(ref _outputWrite);
        Close(ref _inputRead);
        // 输入写端必须留到 Dispose 再关:实测提前关闭会让 ConPTY 认为"终端已断开",
        // 立刻向子进程树广播 Ctrl+C,wslc 还没来得及画第一帧就带着 0xC000013A 退场。
        // 我们不往 stdin 写数据,子进程自然也不会读到东西 —— 保持挂着即可,不影响退出。
        return true;
    }

    /// <summary>等子进程退出;超时或状态异常返回 false(仍可能已退出,调用方自行读码)。</summary>
    public bool WaitForExit(int milliseconds)
    {
        if (_processHandle == IntPtr.Zero) return false;
        return WaitForSingleObject(_processHandle, (uint)milliseconds) == 0;
    }

    /// <summary>子进程退出码;进程还没退出时为 null。</summary>
    public int? ExitCode
    {
        get
        {
            if (_processHandle == IntPtr.Zero) return null;
            if (!GetExitCodeProcess(_processHandle, out var code)) return null;
            return code == StillActive ? null : code;
        }
    }

    private const int StillActive = 259;

    /// <summary>取消:整树终止子进程,并关掉伪控制台让读端尽快 EOF。</summary>
    public void KillTree()
    {
        try
        {
            if (ExitCode is null)
            {
                var process = Process.GetProcessById(_processId);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
        catch (Exception)
        {
            // 进程可能已退出。
        }

        // 拿走句柄再关,保证 ClosePseudoConsole 只执行一次(Dispose 里还会再走一遍这个字段)。
        var pc = Interlocked.Exchange(ref _pseudoConsole, IntPtr.Zero);
        if (pc != IntPtr.Zero) ClosePseudoConsole(pc);
    }

    /// <summary>
    /// 子进程退出后强制拆掉伪控制台,让输出管道的写端全部关闭,读线程才能收尾。
    /// </summary>
    public void CloseConsole()
    {
        var pc = Interlocked.Exchange(ref _pseudoConsole, IntPtr.Zero);
        if (pc != IntPtr.Zero) ClosePseudoConsole(pc);
    }

    private static void Close(ref IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Output?.Dispose();
        if (_pseudoConsole != IntPtr.Zero) { ClosePseudoConsole(_pseudoConsole); _pseudoConsole = IntPtr.Zero; }
        foreach (var handle in new[] { _inputRead, _inputWrite, _outputRead, _outputWrite, _processHandle })
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }

        _processHandle = IntPtr.Zero;
        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
    }

    /// <summary>Win32 命令行引用规则:含空格/引号时加双引号,内部引号反斜杠转义。</summary>
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Contains('"') && !argument.Contains(' ') && !argument.Contains('\t'))
        {
            return argument;
        }

        var sb = new StringBuilder("\"");
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }
            if (i == argument.Length) sb.Append('\\', backslashes * 2);
            else if (argument[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else { sb.Append('\\', backslashes).Append(argument[i]); }
        }

        return sb.Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb; public IntPtr lpReserved; public IntPtr lpDesktop; public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr handle);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr securityAttributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr previousSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? workingDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int which, IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern int WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr handle, out int exitCode);
}
