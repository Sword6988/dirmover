using System.Diagnostics;
using System.Globalization;
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

    /// <summary>指定镜像名是否在本会话中运行。</summary>
    bool IsRunning(string imageName);

    /// <summary>
    /// 取<b>本会话</b>内指定镜像名的进程 ID 快照。
    /// </summary>
    /// <remarks>
    /// 用途是把"结束前的旧 explorer"与"AutoRestartShell 自动拉起的新 explorer"区分开：
    /// 只看镜像名是否在跑，无法分辨"没杀掉"和"已经重启了"，旧实现因此把失败报成成功。
    /// </remarks>
    IReadOnlyList<int> SnapshotSessionProcessIds(string imageName);

    /// <summary>拉起一个进程（不等待）。</summary>
    /// <param name="fileName">可执行文件。</param>
    /// <param name="arguments">参数（可为 null）。</param>
    /// <param name="useShellExecute">
    /// 是否走 shell 通道。**降级路径必须传 false**（直接 CreateProcess）：shell 基础设施
    /// 刚随 explorer 一起死掉时，ShellExecute 通道会失败，此刻唯一可靠的通道就是
    /// <c>CreateProcess</c>。打开目录等"需要 shell 语义"的场景仍传 true。
    /// </param>
    bool Start(string fileName, IReadOnlyList<string>? arguments, bool useShellExecute = true);
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

    public bool IsRunning(string imageName) => SessionProcessIds(imageName, Compat.SessionId()).Count > 0;

    public IReadOnlyList<int> SnapshotSessionProcessIds(string imageName) =>
        SessionProcessIds(imageName, Compat.SessionId());

    /// <summary>
    /// 枚举指定镜像名并<b>按会话收窄</b>。多用户 / RDP 下同机还有别人的 explorer，
    /// 机器级 <c>GetProcessesByName</c> 会把本会话的"没在跑"误报成"在跑"。
    /// 会话号取不到时返回空集（fail-closed）：宁可查不到，也绝不把别的会话算进来。
    /// </summary>
    private static IReadOnlyList<int> SessionProcessIds(string imageName, int sessionId)
    {
        if (sessionId <= 0)
        {
            return Array.Empty<int>();
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(imageName);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException)
        {
            // Win32Exception 也要接住：受限令牌 / AppContainer 下枚举进程列表可能直接失败。
            // 这里不接的话异常会从 RestartExplorerAsync 的 try 块之外冒出去，
            // 变成 UI 层一条"界面命令执行失败"，而不是一条可读的 ShellOpResult 失败文案。
            return Array.Empty<int>();
        }

        var ids = new List<int>(processes.Length);
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    if (!process.HasExited && process.SessionId == sessionId)
                    {
                        ids.Add(process.Id);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or PlatformNotSupportedException)
                {
                    // 进程刚好退出 / 读不到会话号：不计入本会话快照。
                }
            }

            return ids;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    public bool Start(string fileName, IReadOnlyList<string>? arguments, bool useShellExecute = true)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = useShellExecute,
                CreateNoWindow = !useShellExecute,
            };
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

    /// <summary>确认 explorer 退出时的轮询间隔（毫秒）。</summary>
    private const int ExplorerExitPollMilliseconds = 150;

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

            // 失败路径也要关句柄：SEE_MASK_NOCLOSEPROCESS 下 shell 可能已经把 hProcess 回填，
            // 只是启动阶段失败（例如目标进程创建被策略拦下）。不关就是每次失败漏一个句柄。
            if (info.hProcess != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(info.hProcess);
            }

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
    /// 重启本用户会话的资源管理器：结束 → 确认退出 → 拉起（系统未自动拉起时）→ 存活确认。
    /// </summary>
    /// <param name="ct">取消令牌（作用于全部等待）。</param>
    /// <remarks>
    /// 结束命令必须带 <c>/fi "username eq DOMAIN\user"</c> 与 <c>/fi "session eq &lt;本会话&gt;"</c>：
    /// 不带用户过滤会把同机其它会话的桌面一起关掉；<c>taskkill</c> 的 USERNAME 列是
    /// <c>DOMAIN\user</c> 形态，只给 <c>Environment.UserName</c> 在域/本机账户下都匹配不到进程。
    /// <para>
    /// 为什么必须"确认退出"：<c>taskkill</c> 在过滤条件没匹配到任何进程时<b>仍返回 0</b>
    /// （只打印 INFO），而 Windows 的 <c>AutoRestartShell</c> 又会在结束后自动拉起一个新 explorer。
    /// 单次 <c>IsRunning</c> 因此既分不清"旧进程没死"也分不清"新进程已来"——旧实现在这两种
    /// 情况下都会跳过拉起直接返回"重启成功"。这里改为先拍 PID 快照，只认"快照里的 PID 全消失"，
    /// 再判断是否需要由我们显式拉起。
    /// </para>
    /// <para>
    /// 用户名形态在域账号 / AzureAD 账号 / "以其它管理员身份运行"时可能与 USERNAME 列不一致，
    /// 因此过滤条件没匹配到时还有一道<b>按 PID 精确补杀</b>：PID 只取结束前拍到的、此刻仍活着的
    /// 本会话进程，不依赖任何用户名格式，也绝不会跨会话。会话号本身取不到时直接 fail-closed
    /// 返回可操作提示，绝不发出无法按会话收窄的结束命令。
    /// </para>
    /// <para>
    /// 结束态与 <c>AutoRestartShell</c> 开关无关：系统自动拉起时跳过重复拉起（避免多开窗口），
    /// 系统不拉起时由本方法用未提权令牌显式拉起；两种情况下都做存活确认，不存活则补拉一次。
    /// 只要返回 Ok，本会话就必然有一个存活的 explorer。
    /// </para>
    /// </remarks>
    public static async Task<ShellOpResult> RestartExplorerAsync(CancellationToken ct = default)
    {
        if (!Compat.IsWindows())
        {
            return new ShellOpResult(false, UiText.ShellRestartManual);
        }

        int sessionId = Compat.SessionId();
        if (sessionId <= 0)
        {
            // 拿不到会话号时，任何 /fi 都无法保证只作用于本会话：宁可不动 explorer。
            return new ShellOpResult(false, UiText.ShellRestartSessionUnknown);
        }

        // ① 结束：只针对本用户 + 本会话。
        string userName = FilteredUserName();
        var arguments = new List<string>
        {
            "/f",
            "/im",
            "explorer.exe",
            "/fi",
            $"username eq {userName}",
            "/fi",
            $"session eq {sessionId}",
        };

        IReadOnlyList<int> before = Executor.SnapshotSessionProcessIds("explorer");
        Log($"重启explorer: 会话={sessionId} 用户={userName} 结束前本会话explorer PID=[{string.Join(",", before)}]");
        int killExitCode = Executor.Run("taskkill.exe", arguments, Catalog.ExplorerKillTimeout);
        Log($"重启explorer: taskkill 退出码={killExitCode}");
        if (killExitCode != 0 && before.Count > 0)
        {
            return new ShellOpResult(false, UiText.ShellRestartKillFailed(killExitCode));
        }

        // before 为空（本会话本来就没有 explorer，例如被策略禁用或用户在用别的 shell）时，
        // taskkill 报 "not found" 返回 128 —— 那是"没东西可杀"，不是失败。
        // 这种情况要继续往下走拉起阶段，否则用户点了"重启资源管理器"只会拿到一句报错。

        try
        {
            // ② 确认退出：结束前那批 PID 必须全部消失，否则就是"过滤条件没生效"，不能报成功。
            if (!await WaitForExplorerExitAsync(before, ct).ConfigureAwait(false))
            {
                Log("重启explorer: 第一轮退出确认失败（旧 PID 仍在）→ 按 PID 精确补杀");
                // 用户名/会话过滤没匹配到时的确定性兜底：只结束"结束前拍到、此刻仍活着"的本会话 PID。
                IReadOnlyList<int> survivors =
                    CommonPids(before, Executor.SnapshotSessionProcessIds("explorer"));
                if (survivors.Count > 0)
                {
                    Log($"重启explorer: 按 PID 补杀 [{string.Join(",", survivors)}]");
                    KillSessionPids(survivors);
                }

                if (!await WaitForExplorerExitAsync(before, ct).ConfigureAwait(false))
                {
                    Log("重启explorer: 第二轮退出确认仍失败 → 放弃（桌面可能还活着）");
                    return new ShellOpResult(false, UiText.ShellRestartKillNotConfirmed);
                }
            }

            Log("重启explorer: 旧 explorer 已确认退出");

            // ③ 自动拉起宽限：winlogon（AutoRestartShell）重启 shell 不是瞬时的（实机 1–3 s）。
            // 轮询宽限期内没回来，才由我们显式拉起；系统拉了就不再重复（避免多开窗口）。
            bool autoRespawned = await WaitUntilExplorerRunningAsync(
                Catalog.ExplorerAutoRespawnWait, ct).ConfigureAwait(false);
            if (autoRespawned)
            {
                Log($"重启explorer: 系统已自动拉起（宽限 {Catalog.ExplorerAutoRespawnWait.TotalSeconds:F0}s 内）→ 跳过显式拉起");
            }
            else
            {
                Log($"重启explorer: 宽限 {Catalog.ExplorerAutoRespawnWait.TotalSeconds:F0}s 内系统未自动拉起 → 显式拉起");
                if (!LaunchExplorer())
                {
                    Log("重启explorer: 显式拉起失败（两条通道都没起来）→ 报请手动运行");
                    return new ShellOpResult(false, UiText.ShellRestartManual);
                }
            }

            // ④ 存活确认：预算内轮询，出现即算存活；预算用尽补拉一次，再给一轮预算。
            if (!await WaitUntilExplorerRunningAsync(Catalog.ExplorerRelaunchTotalWait, ct).ConfigureAwait(false))
            {
                Log($"重启explorer: 存活确认 {Catalog.ExplorerRelaunchTotalWait.TotalSeconds:F0}s 内未见 explorer → 显式补拉一次");
                if (!LaunchExplorer())
                {
                    Log("重启explorer: 补拉失败 → 报请手动运行");
                    return new ShellOpResult(false, UiText.ShellRestartManual);
                }

                if (!await WaitUntilExplorerRunningAsync(Catalog.ExplorerRelaunchTotalWait, ct).ConfigureAwait(false))
                {
                    Log("重启explorer: 补拉后仍未确认存活 → 报请手动运行");
                    return new ShellOpResult(false, UiText.ShellRestartManual);
                }
            }

            Log("重启explorer: 成功（本会话已有存活的 explorer）");
        }
        catch (OperationCanceledException)
        {
            return new ShellOpResult(false, UiText.ShellRestartCancelled);
        }

        return new ShellOpResult(true, UiText.ShellRestartOk);
    }

    /// <summary>
    /// 轮询等待本会话出现 <c>explorer</c> 进程；预算内出现即返回 <c>true</c>。
    /// </summary>
    /// <remarks>
    /// 替代旧的"固定等 1 s 查一次"：1 s 对 winlogon 自动拉起与进程创建都可能不够，
    /// 查一次的窗口太窄，两种正常的慢都会被误判成失败。
    /// </remarks>
    private static async Task<bool> WaitUntilExplorerRunningAsync(TimeSpan budget, CancellationToken ct)
    {
        long remainingMs = (long)budget.TotalMilliseconds;
        int pollMs = (int)Catalog.ExplorerRelaunchPoll.TotalMilliseconds;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Executor.IsRunning("explorer"))
            {
                return true;
            }

            if (remainingMs <= 0)
            {
                return false;
            }

            int step = (int)Math.Min(pollMs, remainingMs);
            await DelayAsync(step, ct).ConfigureAwait(false);
            remainingMs -= step;
        }
    }

    /// <summary>
    /// 结束命令后轮询等待"结束前那批 explorer PID"全部消失；超时返回 <c>false</c>。
    /// </summary>
    /// <param name="previousPids">结束前拍下的本会话 PID 快照。</param>
    /// <param name="ct">取消令牌。</param>
    private static async Task<bool> WaitForExplorerExitAsync(
        IReadOnlyList<int> previousPids,
        CancellationToken ct)
    {
        if (previousPids.Count == 0)
        {
            // 结束前本会话就没有 explorer：没有需要确认的旧进程，直接进入拉起阶段。
            return true;
        }

        int remaining = (int)Math.Min(Catalog.ExplorerTermWait.TotalMilliseconds, int.MaxValue);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (CommonPids(previousPids, Executor.SnapshotSessionProcessIds("explorer")).Count == 0)
            {
                return true;
            }

            if (remaining <= 0)
            {
                return false;
            }

            int step = Math.Min(ExplorerExitPollMilliseconds, remaining);
            await Task.Delay(step, ct).ConfigureAwait(false);
            remaining -= step;
        }
    }

    /// <summary>
    /// 两个 PID 快照的交集（只针对 PID，不看镜像名，避免新进程被当成旧进程）。
    /// </summary>
    private static IReadOnlyList<int> CommonPids(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return Array.Empty<int>();
        }

        var known = new HashSet<int>(right);
        var common = new List<int>();
        foreach (int pid in left)
        {
            // Remove 既判存在又去重，避免同一 PID 被重复传给 taskkill。
            if (known.Remove(pid))
            {
                common.Add(pid);
            }
        }

        return common;
    }

    /// <summary>
    /// 按 PID 精确结束本会话中"结束前就在、现在还活着"的 explorer。
    /// </summary>
    /// <remarks>
    /// 不看这次 <c>Run</c> 的退出码：PID 可能刚好自行退出（taskkill 会报"没有找到进程"），
    /// 真正的判据是随后那轮"旧 PID 是否全部消失"。参数里没有 <c>/fi</c>，因为 PID 本身
    /// 就是从本会话快照里取出的、比任何用户名过滤都更窄的标识。
    /// </remarks>
    private static void KillSessionPids(IReadOnlyList<int> pids)
    {
        var arguments = new List<string>((pids.Count * 2) + 1) { "/f" };
        foreach (int pid in pids)
        {
            arguments.Add("/pid");
            arguments.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        Executor.Run("taskkill.exe", arguments, Catalog.ExplorerKillTimeout);
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

    /// <summary>
    /// 诊断日志出口（由宿主注入，例如 <see cref="Diagnostics.IAppLog"/>）。
    /// </summary>
    /// <remarks>
    /// 重启资源管理器是全流程里唯一"主动杀掉用户桌面"的环节，失败模式又高度依赖
    /// 目标机器的状态（AutoRestartShell 是否生效、shell 通道是否可用、进程创建多慢）。
    /// 没有这一环节的日志，报障时只能看到一句"无法自动拉起"，无法定位是哪一步、
    /// 哪条通道失败——2026-11-26 的报障正是这种情况。每一步的结论都必须落到这里。
    /// </remarks>
    public static Action<string>? DiagnosticsSink { get; set; }

    /// <summary>测试钩子：替换轮询循环里的 <see cref="Task.Delay"/>（测试不能真等 10 s）。</summary>
    internal static Func<int, CancellationToken, Task>? DelayHook { get; set; }

    private static Task DelayAsync(int milliseconds, CancellationToken ct) =>
        DelayHook is not null ? DelayHook(milliseconds, ct) : Task.Delay(milliseconds, ct);

    private static void Log(string message)
    {
        try
        {
            DiagnosticsSink?.Invoke(message);
        }
        catch
        {
            // 日志出口本身出错不能影响主流程（拉起 explorer 优先于一切）。
        }
    }

    /// <summary>
    /// <c>taskkill</c> 的 USERNAME 过滤值必须是 <c>DOMAIN\user</c> 形态（简报 §"重启资源管理器"）：
    /// 只给裸用户名在域账户 / 本机账户（<c>MACHINE\user</c>）下都匹配不到进程。
    /// </summary>
    private static string FilteredUserName()
    {
        string name = Environment.UserName ?? string.Empty;
        string domain;
        try
        {
            domain = Environment.UserDomainName ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            domain = string.Empty;
        }

        return string.IsNullOrWhiteSpace(domain) ? name : domain + "\\" + name;
    }

    private static bool LaunchExplorer()
    {
        // explorer.exe 在 %WINDIR% 根目录（C:\Windows\explorer.exe，Windows 所有版本一致），
        // **不在** System32 / SysWOW64——用 Environment.SystemDirectory 拼出来的
        // C:\Windows\System32\explorer.exe 不存在，ShellExecute 与 CreateProcess 两条通道
        // 都会以 Win32Error=2（ERROR_FILE_NOT_FOUND）失败。2026-11-26 报障实证：
        // 日志显示两条通道都失败且 ShellExecute 错误码为 2，本机 ls 验证 System32 下确无此文件。
        // 一直没暴露的原因：测试用假钩子不解析真实路径；开发机 AutoRestartShell 拉得快，
        // 走不到显式拉起分支。报障机器恰好系统未自动拉起，才踩中这条坏路径。
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string explorer = Path.Combine(windows, "explorer.exe");
        if (!File.Exists(explorer))
        {
            // 理论不可达（%WINDIR%\explorer.exe 是 Windows 的固定组成）；真发生了也要落日志，
            // 否则又是两条通道一起失败而日志里只有一句"无法自动拉起"。
            Log($"重启explorer: ⚠ 拉起目标不存在：{explorer}");
        }

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

                Log("重启explorer: ShellExecute 通道拉起成功");
                return true;
            }

            // explorer 刚被杀掉时 shell 基础设施正处在半死状态，这条通道失败是"正常失败"，
            // 必须把错误码落日志，才能在报障时分清"通道坏了"还是"文件不在"。
            Log($"重启explorer: ShellExecute 通道失败 Win32Error={Marshal.GetLastWin32Error()}");
        }

        // ② 降级：直接 CreateProcess（UseShellExecute = false）。
        //
        // 降级绝不能再用 ShellExecute 通道：① 与 ② 走同一条路的话，shell 半死时两条
        // 一起失败，"兜底"形同虚设——2026-11-26 报障机器上 explorer 拉不回来，最可疑的
        // 正是这个组合。explorer.exe 的进程创建不需要 shell 代劳，CreateProcess 直启
        // 才是独立于 shell 状态的第二条通道。
        bool started = Executor.Start(explorer, arguments: null, useShellExecute: false);
        Log(started
            ? "重启explorer: CreateProcess 通道拉起成功"
            : "重启explorer: CreateProcess 通道也失败");
        return started;
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
