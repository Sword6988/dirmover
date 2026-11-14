using System.Diagnostics;
using System.Runtime.InteropServices;
using DirMover.Core.Diagnostics;
using DirMover.Core.Interop;
using DirMover.Core.IO;
using DirMover.Core.Registry;

namespace DirMover.Core.Shell;

/// <summary>系统集成操作结论。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Message">说明（失败时必须给出下一步）。</param>
public readonly record struct ShellOpResult(bool Ok, string Message);

/// <summary>
/// 进程执行器（可注入：单测断言参数数组而<b>不真的</b>结束 explorer）。
/// </summary>
/// <remarks>
/// 抽这一层的唯一原因就是"重启资源管理器"这条路径：它必须结束真实进程，而正确性关键在于
/// <c>taskkill</c> 的参数里<b>含有只针对本会话的过滤条件</b>。参数写错会把别的用户的桌面一起关掉，
/// 而这种错误只能靠断言参数数组来抓——真跑一次是破坏性的。
/// </remarks>
internal interface IProcessExecutor
{
    /// <summary>启动进程并等待退出，返回退出码（超时/启动失败返回 -1）。</summary>
    int Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout);

    /// <summary>指定镜像名是否在运行。</summary>
    bool IsRunning(string imageName);

    /// <summary>拉起一个进程（不等待）。</summary>
    bool Start(string fileName, IReadOnlyList<string>? arguments);
}

/// <summary>默认实现：<see cref="Process"/>。</summary>
internal sealed class DefaultProcessExecutor : IProcessExecutor
{
    /// <summary>进程退出后收尾读取残留输出的宽限（毫秒）。</summary>
    private const int DrainGraceMilliseconds = 2000;

    /// <summary>
    /// 启动进程并等待退出，返回退出码（超时/启动失败返回 -1）。
    /// </summary>
    /// <param name="fileName">可执行文件。</param>
    /// <param name="arguments">参数（逐个传入，不拼字符串，避免引号注入）。</param>
    /// <param name="timeout">等待上限。</param>
    /// <remarks>
    /// <b>输出必须并发读取</b>：本方法重定向了 stdout / stderr 却又不读时，子进程一旦写入超过
    /// 管道缓冲（Windows 匿名管道约 4 KB <b>每个流</b>），写端就会阻塞在 <c>WriteFile</c> 上，
    /// 于是子进程永不退出 → 本方法一直等到超时才 <c>Kill</c>。审计 A-06 记录的正是这个形态：
    /// 结论（退出码）没错，但每次都要白等一个完整超时。
    /// <para>
    /// 顺序契约：<b>先起两个读取任务，再等退出</b>。反过来（先等退出、后 <c>ReadToEnd</c>）在
    /// 输出超过缓冲时同样会死锁——这正是历史上常见的教科书错误，两个方向都错。
    /// </para>
    /// </remarks>
    public int Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (string argument in arguments)
            {
                Compat.AddArgument(startInfo, argument);
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                return -1;
            }

            // ① 先起读取（两个流各一个任务，彼此独立），把管道一直抽干。
            Task<string> drainedStdout = process.StandardOutput.ReadToEndAsync();
            Task<string> drainedStderr = process.StandardError.ReadToEndAsync();

            // ② 再等退出。
            if (!process.WaitForExit(ToWaitMilliseconds(timeout)))
            {
                try
                {
                    Compat.KillTree(process);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 已经退出或无权结束：都不影响结论。
                }

                // 收尾：给读取任务一个短宽限（子进程被结束后管道写端关闭，读取会立刻完成）。
                DrainQuietly(drainedStdout, drainedStderr);
                return -1;
            }

            // 进程已退出，但异步读取可能还有尾巴没排空：WaitForExit(ms) 不保证输出读完。
            DrainQuietly(drainedStdout, drainedStderr);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return -1;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// 把超时夹到 <see cref="Process.WaitForExit(int)"/> 能接受的范围内（毫秒，&gt;= 1000）。
    /// </summary>
    /// <param name="timeout">调用方给的超时。</param>
    /// <remarks>
    /// 必须先夹到 <see cref="int.MaxValue"/> 再强转：<c>(int)someDouble</c> 在超出 int 范围时
    /// 在 <c>unchecked</c> 上下文里会静默回绕成负数，于是 <c>WaitForExit(负数)</c> 抛
    /// <c>ArgumentOutOfRangeException</c> —— 而本方法的 catch 过滤器不含它（那里的异常集合是
    /// "启动失败"语义，不该为了一个参数错误扩大）。超过约 24.8 天的超时没有实际意义，
    /// 直接按上限处理。
    /// </remarks>
    private static int ToWaitMilliseconds(TimeSpan timeout)
    {
        double milliseconds = timeout.TotalMilliseconds;
        if (double.IsNaN(milliseconds) || milliseconds < 1000)
        {
            return 1000;
        }

        return milliseconds >= int.MaxValue ? int.MaxValue : (int)milliseconds;
    }

    /// <summary>
    /// 排空两个输出流（带宽限），失败静默忽略。
    /// </summary>
    /// <param name="stdout">标准输出读取任务。</param>
    /// <param name="stderr">标准错误读取任务。</param>
    /// <remarks>
    /// 输出内容<b>不参与</b>结论（<c>IProcessExecutor.Run</c> 只返回退出码，签名不能改），
    /// 这里做的唯一一件事是把管道抽干净，避免子进程因写满缓冲而卡住。因此读取失败
    /// （被 Kill 后管道提前关闭）只当噪音，不改变返回值。
    /// </remarks>
    private static void DrainQuietly(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            Task.WaitAll(new Task[] { stdout, stderr }, DrainGraceMilliseconds);
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
        {
            // 子进程被结束 / 管道句柄已释放：输出无意义，忽略。
        }
    }

    public bool IsRunning(string imageName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(imageName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    public bool Start(string fileName, IReadOnlyList<string>? arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = true };
            if (arguments is not null)
            {
                foreach (string argument in arguments)
                {
                    Compat.AddArgument(startInfo, argument);
                }
            }

            using Process? process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// 系统集成：提权判定与重启、<c>AutoRestartShell</c> 三态、重启资源管理器、打开目录、环境变量广播。
/// </summary>
public static class ShellIntegration
{
    /// <summary><c>HWND_BROADCAST</c>。</summary>
    private const int HwndBroadcast = 0xFFFF;

    /// <summary><c>WM_SETTINGCHANGE</c>。</summary>
    private const uint WmSettingChange = 0x001A;

    /// <summary><c>SMTO_ABORTIFHUNG</c>。</summary>
    private const uint SmtoAbortIfHung = 0x0002;

    /// <summary>UAC 被用户取消的错误码（<c>ERROR_CANCELLED</c>）。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// 进程执行器。单测可替换为"只记录参数"的假实现（本字段 <c>internal</c>，外部不可改）。
    /// </summary>
    internal static IProcessExecutor Executor { get; set; } = new DefaultProcessExecutor();

    /// <summary>
    /// 外壳启动注入点（返回 <c>true</c> = 已成功拉起）。
    /// </summary>
    /// <remarks>
    /// 单测必须能覆盖"逐级降级"这条链路，但真调 <c>ShellExecuteExW</c> 会在测试机上真的拉起
    /// explorer / 弹 UAC。因此把这一次 Win32 调用收在一个可替换的钩子后面：默认 <c>null</c>（真调），
    /// 单测注入一个只记录参数、不启动任何东西的假实现。
    /// </remarks>
    internal static Func<string?, string, string?, bool>? ShellLaunchHook { get; set; }

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated() => NativeMethods.IsUserAnAdmin();

    /// <summary>
    /// 以管理员身份重启自己（<c>ShellExecuteW</c> 的 <c>runas</c> 动词）。
    /// </summary>
    /// <param name="arguments">命令行参数（原样传给新实例）。</param>
    public static ShellOpResult RelaunchAsAdmin(string arguments)
    {
        string? exePathCandidate = Compat.ProcessPath();
        if (string.IsNullOrWhiteSpace(exePathCandidate))
        {
            return new ShellOpResult(false, UiText.ElevationFailed(0));
        }

        // net48 引用程序集缺 NotNullWhen(false)：IsNullOrWhiteSpace 不收窄局部量，
        // 下一行直接传 exePathCandidate 会报 CS8604（net8 侧本就干净）。
        string exePath = exePathCandidate!;

        var info = new NativeMethods.ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<NativeMethods.ShellExecuteInfo>(),
            fMask = NativeMethods.SeeMaskNoCloseProcess | NativeMethods.SeeMaskNoAsync,
            lpVerb = "runas",
            lpFile = exePath,
            lpParameters = arguments ?? string.Empty,
            nShow = NativeMethods.SwShowNormal,
        };

        if (ShellLaunchHook is not null)
        {
            return ShellLaunchHook("runas", exePath, arguments)
                ? new ShellOpResult(true, string.Empty)
                : new ShellOpResult(false, UiText.ElevationCancelled);
        }

        if (!NativeMethods.ShellExecuteExW(ref info))
        {
            int error = Marshal.GetLastWin32Error();
            return new ShellOpResult(
                false,
                error == ErrorCancelled ? UiText.ElevationCancelled : UiText.ElevationFailed(error));
        }

        if (info.hProcess != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(info.hProcess);
        }

        return new ShellOpResult(true, string.Empty);
    }

    /// <summary>
    /// HKLM <c>Winlogon\AutoRestartShell</c> 三态：值=1 → true；值=0 → false；
    /// 值<b>不存在</b> → true（系统默认允许）；读失败 → false（fail-closed，宁可不重启）。
    /// </summary>
    /// <param name="store">注册表封装（只读 HKLM 通道）。</param>
    public static bool AutoRestartShellEnabled(RegistryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            int? value = store.GetHklmDword(Catalog.WinlogonSubKey, "AutoRestartShell");
            if (value is null)
            {
                // 值不存在 = 系统默认行为 = 允许自动重启。
                return true;
            }

            return value.Value != 0;
        }
        catch (RegistryReadException)
        {
            // 读不到就不重启：重启资源管理器是"让改动生效"的便利动作，不值得为它冒险。
            return false;
        }
    }

    /// <summary>
    /// 重启本用户会话的资源管理器：结束 → 静置 1.5 s → 复查 → 未回来则拉起 → 存活确认 1 s。
    /// </summary>
    /// <param name="ct">取消令牌（只影响两段等待）。</param>
    /// <remarks>
    /// 结束命令必须带 <c>/fi "username eq &lt;本用户&gt;"</c>：不带过滤条件会把同机其它会话的
    /// 桌面一起关掉（多用户 / RDP 场景下的真实事故）。
    /// <para>
    /// 拉起顺序：① <c>ShellExecuteExW</c>（<c>lpVerb=nil</c>、<c>SW_SHOWNORMAL</c>、
    /// <c>System32\explorer.exe</c> 绝对路径，走 shell 即不继承提权令牌）→ ② <c>Process.Start</c>
    /// → ③ 都失败则明说"请手动运行 explorer.exe"。
    /// </para>
    /// </remarks>
    public static async Task<ShellOpResult> RestartExplorerAsync(CancellationToken ct = default)
    {
        if (!Compat.IsWindows())
        {
            return new ShellOpResult(false, UiText.ShellRestartManual);
        }

        string userName = Environment.UserName;
        var arguments = new List<string>
        {
            "/f",
            "/im",
            "explorer.exe",
            "/fi",
            $"username eq {userName}",
        };

        Executor.Run("taskkill.exe", arguments, Catalog.ExplorerKillTimeout);

        try
        {
            await Task.Delay(Catalog.ExplorerTermWait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ShellOpResult(false, UiText.ShellRestartCancelled);
        }

        if (!Executor.IsRunning("explorer"))
        {
            if (!LaunchExplorer())
            {
                return new ShellOpResult(false, UiText.ShellRestartManual);
            }

            try
            {
                await Task.Delay(Catalog.ExplorerRelaunchWait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ShellOpResult(false, UiText.ShellRestartCancelled);
            }

            if (!Executor.IsRunning("explorer"))
            {
                return new ShellOpResult(false, UiText.ShellRestartManual);
            }
        }

        return new ShellOpResult(true, UiText.ShellRestartOk);
    }

    /// <summary>用资源管理器打开一个目录。</summary>
    /// <param name="path">目录路径。</param>
    public static ShellOpResult OpenDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ShellOpResult(false, UiText.OpenDirectoryMissing(string.Empty));
        }

        if (!PathTools.Exists(path))
        {
            return new ShellOpResult(false, UiText.OpenDirectoryMissing(path));
        }

        string explorer = Path.Combine(Environment.SystemDirectory, "explorer.exe");

        if (ShellLaunchHook is not null)
        {
            return ShellLaunchHook(null, explorer, path)
                ? new ShellOpResult(true, string.Empty)
                : new ShellOpResult(false, UiText.OpenDirectoryMissing(path));
        }

        var info = new NativeMethods.ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<NativeMethods.ShellExecuteInfo>(),
            fMask = NativeMethods.SeeMaskNoCloseProcess | NativeMethods.SeeMaskNoAsync,
            lpFile = explorer,
            lpParameters = path,
            nShow = NativeMethods.SwShowNormal,
        };

        if (NativeMethods.ShellExecuteExW(ref info))
        {
            if (info.hProcess != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(info.hProcess);
            }

            return new ShellOpResult(true, string.Empty);
        }

        if (Executor.Start("explorer.exe", new[] { path }))
        {
            return new ShellOpResult(true, string.Empty);
        }

        return new ShellOpResult(false, UiText.OpenDirectoryMissing(path));
    }

    /// <summary>
    /// 广播环境变量变更（<c>WM_SETTINGCHANGE</c>）。当前无消费者，保留给后续迭代。
    /// </summary>
    public static Task<ShellOpResult> BroadcastEnvironmentChangeAsync()
    {
        if (!Compat.IsWindows())
        {
            return Task.FromResult(new ShellOpResult(false, UiText.ShellRestartManual));
        }

        IntPtr result;
        IntPtr ok = NativeMethods.SendMessageTimeoutW(
            new IntPtr(HwndBroadcast),
            WmSettingChange,
            IntPtr.Zero,
            "Environment",
            SmtoAbortIfHung,
            5000,
            out result);

        return Task.FromResult(new ShellOpResult(ok != IntPtr.Zero, string.Empty));
    }

    // ================================================================ 内部

    private static bool LaunchExplorer()
    {
        string explorer = Path.Combine(Environment.SystemDirectory, "explorer.exe");

        // ① 走 shell（lpVerb = nil）：不继承本进程的提权令牌，否则会拉起一个高权限桌面。
        if (ShellLaunchHook is not null)
        {
            if (ShellLaunchHook(null, explorer, null))
            {
                return true;
            }
        }
        else
        {
            var info = new NativeMethods.ShellExecuteInfo
            {
                cbSize = Marshal.SizeOf<NativeMethods.ShellExecuteInfo>(),
                fMask = NativeMethods.SeeMaskNoCloseProcess | NativeMethods.SeeMaskNoAsync,
                lpVerb = null,
                lpFile = explorer,
                nShow = NativeMethods.SwShowNormal,
            };

            if (NativeMethods.ShellExecuteExW(ref info))
            {
                if (info.hProcess != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(info.hProcess);
                }

                return true;
            }
        }

        // ② 降级：直接启动绝对路径。
        return Executor.Start(explorer, arguments: null);
    }
}

/// <summary>
/// 单实例守卫：命名互斥体 <c>Local\DirMover_Single</c> + PID 文件。
/// </summary>
/// <remarks>
/// 判"已在运行"的顺序是刻意的：<b>先看 PID 文件里记的进程是否还活着且镜像名是 DirMover</b>，
/// 再看互斥体。原因有两个：
/// ① 互斥体在"持有者崩溃"后能否枚举到依实现而异，而 PID + 镜像名是确定信息；
/// ② 只靠互斥体会把"上次异常退出留下的句柄"当成"正在运行"，于是用户再也打不开程序。
/// <para>
/// PID 文件恒为 <c>%LOCALAPPDATA%\DirMover\single_instance.pid</c>（由 <see cref="IAppPaths"/> 给出），
/// 不随便携落点变化——否则换目录运行就会丢掉"上一个实例"的线索，允许两个实例同时迁移。
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string ImageName = "DirMover";

    private static readonly object Gate = new();

    private static bool _heldByThisProcess;

    private readonly Mutex? _mutex;
    private readonly bool _ownsMutex;
    private readonly bool _isOwner;
    private readonly string _pidFile;
    private bool _released;

    private SingleInstanceGuard(Mutex? mutex, bool ownsMutex, string pidFile, bool isOwner)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _pidFile = pidFile;
        _isOwner = isOwner;
    }

    /// <summary>
    /// 尝试获取单实例权。
    /// </summary>
    /// <param name="paths">落点（决定 PID 文件位置）。</param>
    /// <param name="log">日志出口。</param>
    /// <param name="alreadyRunning">
    /// 是否已有<b>活着的</b>实例。为 <c>true</c> 时调用方应提示"程序已在运行"并退出。
    /// </param>
    public static SingleInstanceGuard Acquire(IAppPaths paths, IAppLog log, out bool alreadyRunning)
    {
        IAppLog safeLog = log ?? NullLog.Instance;
        string pidFile = paths?.SingleInstancePidFile
            ?? Path.Combine(Path.GetTempPath(), ImageName, "single_instance.pid");

        lock (Gate)
        {
            if (_heldByThisProcess)
            {
                // 同一进程内重复获取：互斥体在同线程上是可重入的，靠它判不出来。
                alreadyRunning = true;
                return new SingleInstanceGuard(null, false, pidFile, isOwner: false);
            }

            int ownerPid = ReadPid(pidFile);
            bool ownerAlive = IsLiveDirMoverInstance(ownerPid) && ownerPid != Compat.ProcessId();

            Mutex? mutex = null;
            bool ownsMutex = false;
            try
            {
                mutex = new Mutex(initiallyOwned: true, Catalog.SingleInstanceName, out bool createdNew);
                ownsMutex = createdNew || SafeWait(mutex);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                safeLog.Warn($"单实例互斥体不可用，改为仅按 PID 文件判断：{ex.Message}");
                mutex?.Dispose();
                mutex = null;
            }

            if (!ownsMutex && ownerAlive)
            {
                alreadyRunning = true;
                mutex?.Dispose();
                return new SingleInstanceGuard(null, false, pidFile, isOwner: false);
            }

            if (!ownsMutex)
            {
                // 互斥体被别人持有但持有者已不是活着的 DirMover（异常退出 / PID 被复用）：
                // 不能因此把用户挡在外面，接管 PID 文件继续启动。
                safeLog.Warn($"单实例互斥体已被占用，但记录中的实例（PID {ownerPid}）已不在，按新实例启动");
            }

            _heldByThisProcess = true;
            alreadyRunning = false;
            TryWritePid(pidFile, safeLog);

            return new SingleInstanceGuard(mutex, ownsMutex, pidFile, isOwner: true);
        }
    }

    /// <summary>释放单实例权（幂等）。</summary>
    public void Release()
    {
        lock (Gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;

            // 被拒绝的第二个实例不得清理真正持有者的 PID 文件。
            if (!_isOwner)
            {
                _mutex?.Dispose();
                return;
            }

            if (_ownsMutex && _mutex is not null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // 不是当前线程持有的互斥体：忽略（进程即将退出，句柄随后释放）。
                }
            }

            _mutex?.Dispose();

            try
            {
                if (File.Exists(_pidFile))
                {
                    File.Delete(_pidFile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉也无害：下次启动会因"PID 已死"判为陈旧并接管。
            }

            _heldByThisProcess = false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Release();

    // ================================================================ 内部

    private static bool SafeWait(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者崩溃：互斥体所有权已转给我们。
            return true;
        }
    }

    private static int ReadPid(string pidFile)
    {
        try
        {
            if (!File.Exists(pidFile))
            {
                return 0;
            }

            string text = File.ReadAllText(pidFile).Trim();
            return int.TryParse(text, out int pid) ? pid : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private static bool IsLiveDirMoverInstance(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            // PID 会被复用：镜像名不是 DirMover 就说明那个 PID 已经不属于我们。
            return Compat.Contains(process.ProcessName, ImageName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryWritePid(string pidFile, IAppLog log)
    {
        try
        {
            string? directory = Path.GetDirectoryName(pidFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(pidFile, Compat.ProcessId().ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // PID 文件写不进去只影响"下次能否识别本次实例"，不阻断启动。
            log.Warn($"单实例 PID 文件写入失败：{pidFile}；{ex.Message}");
        }
    }
}
