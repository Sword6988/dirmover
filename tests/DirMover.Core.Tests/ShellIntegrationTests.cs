using DirMover.Core.Diagnostics;
using DirMover.Core.Registry;
using DirMover.Core.Shell;

namespace DirMover.Core.Tests;

/// <summary>T12：系统集成（<c>AutoRestartShell</c> 三态、重启 explorer 的参数与降级、打开目录、单实例）。</summary>
public sealed class ShellIntegrationTests
{
    [Fact]
    public void AutoRestartShell_ValueOne_IsEnabled()
    {
        var store = new RegistryStore(
            new RegistrySeed().AddHklm(Catalog.WinlogonSubKey, "AutoRestartShell", 1).ToBackend(),
            new FakeLog());

        Assert.True(ShellIntegration.AutoRestartShellEnabled(store));
    }

    [Fact]
    public void AutoRestartShell_ValueZero_IsDisabled()
    {
        var store = new RegistryStore(
            new RegistrySeed().AddHklm(Catalog.WinlogonSubKey, "AutoRestartShell", 0).ToBackend(),
            new FakeLog());

        Assert.False(ShellIntegration.AutoRestartShellEnabled(store));
    }

    [Fact]
    public void AutoRestartShell_MissingValue_DefaultsToEnabled()
    {
        var store = new RegistryStore(new RegistrySeed().ToBackend(), new FakeLog());

        Assert.True(ShellIntegration.AutoRestartShellEnabled(store));
    }

    [Fact]
    public void AutoRestartShell_ReadFailure_IsDisabledFailClosed()
    {
        var store = new RegistryStore(new ThrowingReadBackend(), new FakeLog());

        Assert.False(ShellIntegration.AutoRestartShellEnabled(store));
    }

    [Fact]
    public async Task RestartExplorer_KillsOnlyCurrentUserSession()
    {
        var executor = new FakeProcessExecutor();
        executor.Snapshots.Enqueue(new[] { 4242 });   // 结束前：本会话有一个 explorer
        executor.Snapshots.Enqueue(Array.Empty<int>()); // 结束后：旧 PID 已消失
        executor.RunningResults.Enqueue(true);        // 系统已自动拉起 → 不重复拉起
        executor.RunningResults.Enqueue(true);        // 存活确认通过
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.True(result.Ok);
        Assert.Equal(UiText.ShellRestartOk, result.Message);
        (string file, IReadOnlyList<string> arguments) = Assert.Single(executor.Runs);
        Assert.Equal("taskkill.exe", file);
        Assert.Contains("/f", arguments);
        Assert.Contains("/im", arguments);
        Assert.Contains("explorer.exe", arguments);
        Assert.Equal(2, arguments.Count(argument => argument == "/fi")); // 用户 + 会话两个过滤

        // USERNAME 列是 DOMAIN\user；只给裸用户名会让过滤器一个进程都匹配不到（本次 Bug 1 根因）。
        string expectedUser = string.IsNullOrWhiteSpace(Environment.UserDomainName)
            ? Environment.UserName
            : Environment.UserDomainName + "\\" + Environment.UserName;
        Assert.Contains(arguments, argument =>
            string.Equals(argument, $"username eq {expectedUser}", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(Environment.UserDomainName))
        {
            Assert.DoesNotContain(arguments, argument =>
                string.Equals(argument, $"username eq {Environment.UserName}", StringComparison.OrdinalIgnoreCase));
        }

        // 还得按会话收窄：同一账号多会话登录时不能把别的会话桌面一起关掉。
        int sessionId = Compat.SessionId();
        if (sessionId > 0)
        {
            Assert.Contains(arguments, argument =>
                string.Equals(argument, $"session eq {sessionId}", StringComparison.Ordinal));
        }
        else
        {
            // 取不到会话号时按设计退回"不按会话过滤"，此时不应伪造一个 session 过滤。
            Assert.DoesNotContain(arguments, argument =>
                argument.StartsWith("session eq ", StringComparison.OrdinalIgnoreCase));
        }

        // 结束前拍快照 + 结束后至少复查一次（旧 PID 已确认消失）；两次存活判定
        // （拉起判定 + 存活确认）都必须发生，不能因为"没显式拉起"就跳过。
        Assert.Empty(executor.Snapshots);
        Assert.Empty(executor.RunningResults);
        Assert.Empty(scope.LaunchAttempts); // 系统已拉起，不重复开窗口
        Assert.Empty(executor.Starts);
    }

    [Theory]
    [InlineData(1)]   // 命令本身失败
    [InlineData(-1)]  // 启动失败 / 超时（Run 的哨兵值）
    public async Task RestartExplorer_KillCommandFails_ReportsActionableFailure(int exitCode)
    {
        var executor = new FakeProcessExecutor();
        executor.Snapshots.Enqueue(new[] { 4242 });
        executor.RunResults.Enqueue(exitCode); // taskkill 非零：命令本身失败
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.False(result.Ok);
        Assert.Contains($"taskkill 退出码 {exitCode}", result.Message);
        Assert.Contains("请", result.Message); // 必须给出下一步
        Assert.Empty(scope.LaunchAttempts);   // 结束都失败了，不能继续拉起
        Assert.Empty(executor.Starts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RestartExplorer_SessionIdUnknown_FailsClosedWithoutTouchingExplorer(int sessionId)
    {
        // 取不到会话号时，/fi 无法保证只作用于本会话：宁可不动 explorer，也不能误伤
        // 同机其它会话（域环境 / 服务会话 / RDP 都可能拿不到有效 SessionId）。
        // 这类"机器差异"正是报障机器与构建机不同时必须守住的边界。
        var executor = new FakeProcessExecutor();
        using var scope = ShellTestScope.Create(executor, sessionId: sessionId);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.False(result.Ok);
        Assert.Equal(UiText.ShellRestartSessionUnknown, result.Message);
        Assert.Contains("请", result.Message);       // 必须给出可操作下一步
        Assert.Empty(executor.Runs);                // 一次 taskkill 都不许发
        Assert.Empty(scope.LaunchAttempts);         // 也不许拉起
        Assert.Empty(executor.Starts);
    }

    [Fact]
    public async Task RestartExplorer_OldExplorerSurvives_ReportsFailureInsteadOfFakeSuccess()
    {
        // 复现用户现场：AutoRestartShell 默认开启，taskkill 的 /fi 没匹配到进程时仍退出 0，
        // 1.5 s 后本会话里 explorer 又"在跑"（旧进程没死 / 系统已拉起），旧实现只看这次
        // IsRunning == true 就跳过拉起并直接 return ShellRestartOk —— 界面报成功，流程没走完。
        var executor = new FakeProcessExecutor { SteadySnapshot = new[] { 4242 } };
        executor.Snapshots.Enqueue(new[] { 4242 }); // 结束前快照；之后稳态仍是 4242（旧 PID 一直没消失）
        executor.RunningResults.Enqueue(true);      // 关键：1.5 s 后 IsRunning 为 true，正是旧实现假成功的条件
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        // 修复后必须以"旧 PID 快照是否全部消失"为准，不能让任何形式的 IsRunning 短路成成功。
        Assert.False(result.Ok);
        Assert.Equal(UiText.ShellRestartKillNotConfirmed, result.Message);
        Assert.NotEqual(UiText.ShellRestartOk, result.Message);
        // 结束命令发出过第一次（/fi 过滤），随后"旧 PID 仍在"会触发一次按 PID 的确定性兜底
        // （/f /pid 4242）；两轮确认都失败后必须报失败，绝不能以 IsRunning==true 短路成成功。
        Assert.Equal(2, executor.Runs.Count);
        Assert.Contains("/fi", executor.Runs[0].Item2);
        Assert.Contains("/pid", executor.Runs[1].Item2);
        Assert.Contains("4242", executor.Runs[1].Item2);
        Assert.Empty(scope.LaunchAttempts); // 旧进程还在，不允许跳过确认去拉起
        Assert.Empty(executor.Starts);
    }

    [Fact]
    public async Task RestartExplorer_AutoRestartShellNewPid_IsConfirmedAsTerminated()
    {
        // 结束前是 4242，结束后系统自动拉起 9999：PID 不同 ⇒ 旧进程确实退出了，
        // 不能因为 IsRunning 仍为 true 就判定"没杀掉"。
        var executor = new FakeProcessExecutor();
        executor.Snapshots.Enqueue(new[] { 4242 });
        executor.Snapshots.Enqueue(new[] { 9999 });
        executor.RunningResults.Enqueue(true);
        executor.RunningResults.Enqueue(true);
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.True(result.Ok);
        Assert.Equal(UiText.ShellRestartOk, result.Message);
        Assert.Empty(scope.LaunchAttempts);
    }

    [Fact]
    public async Task RestartExplorer_AutoRestartShellDies_ThenRelaunchRecovers()
    {
        // 设计取舍验收：系统自动拉起时"不重复显式拉起"，但存活确认必做。
        // 这里让自动拉起的实例在存活确认时一直查不到（预算用尽）→ 必须补拉一次并复查成功。
        var executor = new FakeProcessExecutor();
        executor.Snapshots.Enqueue(new[] { 4242 });       // 旧 PID
        executor.Snapshots.Enqueue(Array.Empty<int>());   // 旧 PID 已确认消失
        executor.RunningResults.Enqueue(true);            // 自动拉起宽限内出现 → 不显式拉起
        executor.RunningResults.Enqueue(false);           // 存活确认：前三次轮询都没有
        executor.RunningResults.Enqueue(false);
        executor.RunningResults.Enqueue(false);
        // 之后的稳态仍是 false：存活预算耗尽 → 显式补拉（钩子成功即模拟存活）→ 复查通过。
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.True(result.Ok);
        Assert.Equal(UiText.ShellRestartOk, result.Message);
        Assert.Single(scope.LaunchAttempts); // 只在存活预算耗尽后补拉一次，不重复开窗口
        Assert.Empty(executor.RunningResults);
        Assert.Empty(executor.Snapshots);
    }

    [Fact]
    public async Task RestartExplorer_LaunchedExplorerDoesNotSurvive_RunExplorerManually()
    {
        // 拉起命令"成功"但进程随即退出：预算内一直查不到，补拉一次，两次都不存活才给出手动指引。
        var executor = new FakeProcessExecutor { LaunchMakesRunning = false };
        executor.Snapshots.Enqueue(new[] { 4242 });
        executor.Snapshots.Enqueue(Array.Empty<int>());
        executor.RunningResults.Enqueue(false); // 自动拉起宽限：查不到 → 显式拉起
        executor.RunningResults.Enqueue(false);
        executor.RunningResults.Enqueue(false);
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.False(result.Ok);
        Assert.Contains("请手动运行 explorer.exe", result.Message);
        Assert.Equal(2, scope.LaunchAttempts.Count);
        Assert.Empty(executor.Starts);
    }

    [Fact]
    public async Task RestartExplorer_ShellLaunchSucceeds_NoFallbackUsed()
    {
        var executor = new FakeProcessExecutor();
        executor.Snapshots.Enqueue(new[] { 100 }); // 旧 PID
        executor.Snapshots.Enqueue(Array.Empty<int>()); // 确认退出
        executor.RunningResults.Enqueue(false); // 自动拉起宽限：查不到 → 显式拉起
        executor.RunningResults.Enqueue(false);
        executor.RunningResults.Enqueue(false);
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.True(result.Ok);
        Assert.Single(scope.LaunchAttempts);
        Assert.Empty(executor.Starts);

        // 路径回归（2026-11-26 报障）：explorer.exe 在 %WINDIR% 根目录，不在 System32——
        // SystemDirectory 拼出的路径不存在，两条拉起通道都会以 Win32Error=2 失败。
        (string? _, string launchedFile, string? _) = Assert.Single(scope.LaunchAttempts);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            launchedFile,
            ignoreCase: true);
    }

    [Fact]
    public async Task RestartExplorer_ShellFails_FallsBackToCreateProcess()
    {
        var executor = new FakeProcessExecutor { StartResult = true };
        executor.Snapshots.Enqueue(new[] { 100 });
        executor.Snapshots.Enqueue(Array.Empty<int>());
        executor.RunningResults.Enqueue(false); // 自动拉起宽限：查不到 → 显式拉起
        executor.RunningResults.Enqueue(false);
        executor.RunningResults.Enqueue(false);
        using var scope = ShellTestScope.Create(executor, shellLaunchResult: false);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.True(result.Ok);
        (string file, _, bool useShellExecute) = Assert.Single(executor.Starts);
        Assert.EndsWith("explorer.exe", file, StringComparison.OrdinalIgnoreCase);

        // 降级必须走 CreateProcess（UseShellExecute=false）：shell 刚死时 ShellExecute 通道
        // 不可用，"降级"若还走同一条通道就形同虚设——2026-11-26 报障的核心嫌疑。
        Assert.False(useShellExecute);
    }

    [Fact]
    public async Task RestartExplorer_ShellAndFallbackFail_SaysRunManually()
    {
        var executor = new FakeProcessExecutor { StartResult = false };
        executor.Snapshots.Enqueue(new[] { 100 });
        executor.Snapshots.Enqueue(Array.Empty<int>());
        executor.RunningResults.Enqueue(false); // 第一次拉起前未在运行
        using var scope = ShellTestScope.Create(executor, shellLaunchResult: false);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.False(result.Ok);
        Assert.Contains("请手动运行 explorer.exe", result.Message);
        Assert.Single(executor.Starts);
    }

    [Fact]
    public async Task RestartExplorer_FailurePath_EmitsStepByStepDiagnostics()
    {
        // 2026-11-26 报障的教训：重启环节零日志，报障机器上只剩一句"无法自动拉起"，
        // 是哪一步、哪条通道失败完全无从判断。分步日志由此成为硬性契约：
        // 任何失败路径都必须能在日志里重建全过程。
        var executor = new FakeProcessExecutor { StartResult = false, LaunchMakesRunning = false };
        executor.Snapshots.Enqueue(new[] { 100 });
        executor.Snapshots.Enqueue(Array.Empty<int>());
        executor.RunningResults.Enqueue(false); // 自动拉起宽限：查不到 → 显式拉起
        using var scope = ShellTestScope.Create(executor, shellLaunchResult: false);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.False(result.Ok);
        Assert.Contains(scope.Logs, m => m.Contains("结束前本会话explorer PID=[100]"));
        Assert.Contains(scope.Logs, m => m.Contains("taskkill 退出码=0"));
        Assert.Contains(scope.Logs, m => m.Contains("旧 explorer 已确认退出"));
        Assert.Contains(scope.Logs, m => m.Contains("系统未自动拉起"));
        Assert.Contains(scope.Logs, m => m.Contains("CreateProcess 通道也失败"));
    }

    [Fact]
    public void RelaunchAsAdmin_UsesRunasVerb()
    {
        var executor = new FakeProcessExecutor();
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = ShellIntegration.RelaunchAsAdmin("--dry-run");

        Assert.True(result.Ok);
        (string? verb, _, string? arguments) = Assert.Single(scope.LaunchAttempts);
        Assert.Equal("runas", verb);
        Assert.Equal("--dry-run", arguments);
    }

    [Fact]
    public void RelaunchAsAdmin_Cancelled_ReportsCancellation()
    {
        var executor = new FakeProcessExecutor();
        using var scope = ShellTestScope.Create(executor, shellLaunchResult: false);

        ShellOpResult result = ShellIntegration.RelaunchAsAdmin(string.Empty);

        Assert.False(result.Ok);
        Assert.Contains("取消", result.Message);
    }

    [Fact]
    public async Task RestartExplorer_Cancelled_DoesNotReportSuccess()
    {
        var executor = new FakeProcessExecutor { SteadySnapshot = new[] { 4242 } };
        executor.Snapshots.Enqueue(new[] { 4242 });
        using var scope = ShellTestScope.Create(executor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync(cts.Token);

        Assert.False(result.Ok);
        Assert.Contains("取消", result.Message);
        Assert.Empty(scope.LaunchAttempts);
    }

    [Fact]
    public void Compat_AddArgument_KeepsSpacedFilterAsOneArgument()
    {
        // Bug 1 排查时怀疑过"含空格的参数被拆成 3 个 token"。net8 侧走 ArgumentList，
        // 一个元素 = 一个参数，Process 会在拼命令行时自动加引号；net48 侧 QuoteArgument
        // 同样对含空格的参数加引号。两个目标都正确，这条用例钉住"不许退化成拼字符串"。
        var startInfo = new System.Diagnostics.ProcessStartInfo("taskkill.exe");
        Compat.AddArgument(startInfo, @"username eq DOMAIN\user");

        Assert.Single(startInfo.ArgumentList);
        Assert.Equal(@"username eq DOMAIN\user", startInfo.ArgumentList[0]);
    }

    [Fact]
    public void OpenDirectory_MissingPath_ReturnsFailureInsteadOfThrowing()
    {
        using var sandbox = new TempDirectory("shell-open-missing");
        var executor = new FakeProcessExecutor();
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = ShellIntegration.OpenDirectory(sandbox.Combine("nope"));

        Assert.False(result.Ok);
        Assert.Contains("无法打开目录", result.Message);
        Assert.Empty(scope.LaunchAttempts);
    }

    [Fact]
    public void OpenDirectory_ExistingPath_UsesExplorer()
    {
        using var sandbox = new TempDirectory("shell-open");
        string directory = sandbox.CreateDirectoryPath("logs");
        var executor = new FakeProcessExecutor();
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = ShellIntegration.OpenDirectory(directory);

        Assert.True(result.Ok);
        (string? verb, string file, string? parameters) = Assert.Single(scope.LaunchAttempts);
        Assert.Null(verb);
        Assert.EndsWith("explorer.exe", file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(directory, parameters);
    }

    [Fact]
    public void SingleInstance_SecondAcquire_ReportsAlreadyRunningAndKeepsPidFile()
    {
        using var sandbox = new TempDirectory("single-instance");
        var paths = new TempAppPaths(sandbox.CreateDirectoryPath("app"));
        var log = new FakeLog();

        SingleInstanceGuard first = SingleInstanceGuard.Acquire(paths, log, out bool firstRunning);
        try
        {
            Assert.False(firstRunning);
            Assert.True(File.Exists(paths.SingleInstancePidFile));
            Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(paths.SingleInstancePidFile));

            using (SingleInstanceGuard second = SingleInstanceGuard.Acquire(paths, log, out bool secondRunning))
            {
                Assert.True(secondRunning);
            }

            // 被拒绝的实例不得清理真正持有者的 PID 文件。
            Assert.True(File.Exists(paths.SingleInstancePidFile));
        }
        finally
        {
            first.Dispose();
        }

        Assert.False(File.Exists(paths.SingleInstancePidFile));
    }

    [Fact]
    public void SingleInstance_DeadOwnerPid_AllowsStartup()
    {
        using var sandbox = new TempDirectory("single-instance-stale");
        var paths = new TempAppPaths(sandbox.CreateDirectoryPath("app"));
        var log = new FakeLog();
        File.WriteAllText(paths.SingleInstancePidFile, "2147483600");

        SingleInstanceGuard guard = SingleInstanceGuard.Acquire(paths, log, out bool alreadyRunning);
        try
        {
            Assert.False(alreadyRunning);
            Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(paths.SingleInstancePidFile));
        }
        finally
        {
            guard.Dispose();
        }
    }

    [Fact]
    public void SingleInstance_PidFile_IsIndependentOfPortableLocation()
    {
        using var sandbox = new TempDirectory("single-instance-pid");
        string first = sandbox.CreateDirectoryPath("a");
        string second = sandbox.CreateDirectoryPath("b");

        DefaultAppPaths firstPaths = DefaultAppPaths.Resolve(first);
        DefaultAppPaths secondPaths = DefaultAppPaths.Resolve(second);

        Assert.Equal(firstPaths.SingleInstancePidFile, secondPaths.SingleInstancePidFile);
        Assert.EndsWith("single_instance.pid", firstPaths.SingleInstancePidFile, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DirMover", firstPaths.SingleInstancePidFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultProcessExecutor_LargeOutput_ReturnsWithoutWaitingForTheTimeout()
    {
        // 审计 A-06 的回归用例。缺陷形态：Run 重定向了 stdout / stderr 却从不读取，
        // 子进程写满管道缓冲（Windows 匿名管道约 4 KB）后阻塞在写入上 → 永不退出 →
        // Run 只能等到超时才 Kill。结论（退出码）一直是对的，坏的是"每次白等一个完整超时"。
        // 这里让子进程输出约 24 KB（远超 4 KB），断言它很快返回。
        var executor = new DefaultProcessExecutor();
        TimeSpan timeout = TimeSpan.FromSeconds(20);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int exitCode = executor.Run(
            "cmd.exe",
            new[] { "/c", "for /l %i in (1,1,600) do @echo 0123456789012345678901234567890123456789" },
            timeout);
        stopwatch.Stop();

        Assert.Equal(0, exitCode);

        // 正常应在 1 s 内返回；给到 10 s 余量（冷启动、杀软扫描第一支进程），仍远小于 20 s 超时，
        // 所以"退化成阻塞到超时"这条回归必然打红。反过来也不能断言"必须 < 1 s"，那会在慢机器上飘。
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Run 耗时 {stopwatch.Elapsed.TotalSeconds:F1} s（超时上限 {timeout.TotalSeconds:F0} s），疑似输出未并发读取导致阻塞");
    }

    [Fact]
    public void DefaultProcessExecutor_LargeStderrOutput_ReturnsWithoutWaitingForTheTimeout()
    {
        // A-06 回归用例的另一半。修复契约是"stdout / stderr 两个流都并发抽干"，而上一条用例
        // 只让子进程压 stdout —— 恰好抓不到"只修了一个流"的半修。本机实测（对照实现）：
        // 只读 stdout、不管 stderr 时，stderr 写满缓冲同样卡住写入，Run 要白等整整一个
        // Catalog.ExplorerKillTimeout（20.01 s）；两个流都读时 0.08 s 返回。
        // 所以这条用例不是重复：它钉住的是"stderr 也被读"这个独立事实。
        var executor = new DefaultProcessExecutor();
        TimeSpan timeout = TimeSpan.FromSeconds(20);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int exitCode = executor.Run(
            "cmd.exe",
            new[] { "/c", "for /l %i in (1,1,600) do @echo 0123456789012345678901234567890123456789 1>&2" },
            timeout);
        stopwatch.Stop();

        Assert.Equal(0, exitCode);

        // 余量口径与上一条一致：正常 < 1 s，给到 10 s 容忍冷启动/杀软，仍远小于 20 s 超时。
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Run 耗时 {stopwatch.Elapsed.TotalSeconds:F1} s（超时上限 {timeout.TotalSeconds:F0} s），疑似 stderr 未并发读取");
    }

    /// <summary>替换进程执行器、shell 启动钩子、轮询延迟与会话号，用例结束后全部恢复。</summary>
    private sealed class ShellTestScope : IDisposable
    {
        private readonly IProcessExecutor _originalExecutor;
        private readonly Func<string?, string, string?, bool>? _originalHook;
        private readonly int? _originalSessionId;
        private readonly Func<int, CancellationToken, Task>? _originalDelay;
        private readonly Action<string>? _originalSink;

        private ShellTestScope(IProcessExecutor executor, bool? shellLaunchResult, int sessionId)
        {
            _originalExecutor = ShellIntegration.Executor;
            _originalHook = ShellIntegration.ShellLaunchHook;
            _originalSessionId = Compat.SessionIdOverride;
            _originalDelay = ShellIntegration.DelayHook;
            _originalSink = ShellIntegration.DiagnosticsSink;
            ShellIntegration.Executor = executor;

            // 会话号固定注入：CI 常跑在会话 0，不注入就无法确定性地验证"按会话收窄"。
            Compat.SessionIdOverride = sessionId;

            // 轮询循环（自动拉起宽限 3 s / 存活确认 10 s）在测试里不能真等：
            // 延迟替换为立即完成，让"预算用尽"只取决于 FakeProcessExecutor 的应答序列。
            ShellIntegration.DelayHook = (_, _) => Task.CompletedTask;

            // 分步日志捕获：既防泄漏到其它用例，也让用例可以直接断言日志内容。
            ShellIntegration.DiagnosticsSink = message => Logs.Add(message);

            bool hookSuccess = shellLaunchResult ?? true;
            ShellIntegration.ShellLaunchHook = (verb, file, arguments) =>
            {
                LaunchAttempts.Add((verb, file, arguments));
                if (hookSuccess && executor is FakeProcessExecutor fake && fake.LaunchMakesRunning)
                {
                    // 轮询语义下，"拉起成功"意味着之后 IsRunning 应当为 true。
                    // 是否翻转由 FakeProcessExecutor.LaunchMakesRunning 控制：
                    // 默认 true（真实世界：explorer 一旦启动就会存活）；
                    // "拉起后立刻死掉"的用例把它设为 false。
                    fake.SimulateLaunchedProcessSurvives();
                }

                return hookSuccess;
            };
        }

        internal List<(string? Verb, string File, string? Arguments)> LaunchAttempts { get; } = new();

        internal List<string> Logs { get; } = new();

        internal static ShellTestScope Create(
            IProcessExecutor executor,
            bool? shellLaunchResult = null,
            int sessionId = 1) => new(executor, shellLaunchResult, sessionId);

        public void Dispose()
        {
            ShellIntegration.Executor = _originalExecutor;
            ShellIntegration.ShellLaunchHook = _originalHook;
            ShellIntegration.DelayHook = _originalDelay;
            ShellIntegration.DiagnosticsSink = _originalSink;
            Compat.SessionIdOverride = _originalSessionId;
        }
    }

    /// <summary>记录参数、不真的启动任何进程。</summary>
    private sealed class FakeProcessExecutor : IProcessExecutor
    {
        internal List<(string File, IReadOnlyList<string> Arguments)> Runs { get; } = new();

        internal List<(string File, IReadOnlyList<string>? Arguments, bool UseShellExecute)> Starts { get; } = new();

        /// <summary>依次返回的"本会话 PID 快照"；用尽后返回 <see cref="SteadySnapshot"/>。</summary>
        internal Queue<IReadOnlyList<int>> Snapshots { get; } = new();

        /// <summary>依次返回的存活判定；用尽后返回 <see cref="SteadyRunning"/>。</summary>
        internal Queue<bool> RunningResults { get; } = new();

        /// <summary>依次返回的 <c>Run</c> 退出码；默认 0。</summary>
        internal Queue<int> RunResults { get; } = new();

        /// <summary>PID 快照队列用尽后的稳态值（模拟"旧进程一直没死"）。</summary>
        internal IReadOnlyList<int> SteadySnapshot { get; set; } = Array.Empty<int>();

        /// <summary>存活队列用尽后的稳态值。</summary>
        internal bool SteadyRunning { get; set; }

        /// <summary>拉起成功后是否模拟"explorer 存活"（后续 IsRunning 返回 true）。</summary>
        /// <remarks>
        /// 轮询语义下"拉起 → 进程存活"是两个独立事实：默认 true（正常世界）；
        /// "拉起后立刻退出"的用例（LaunchedExplorerDoesNotSurvive）设为 false。
        /// </remarks>
        internal bool LaunchMakesRunning { get; set; } = true;

        internal bool StartResult { get; set; } = true;

        /// <summary>模拟"一次成功的拉起之后，进程在本会话存活"。</summary>
        internal void SimulateLaunchedProcessSurvives() => SteadyRunning = true;

        public int Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Runs.Add((fileName, arguments));
            return RunResults.Count > 0 ? RunResults.Dequeue() : 0;
        }

        public bool IsRunning(string imageName) =>
            RunningResults.Count > 0 ? RunningResults.Dequeue() : SteadyRunning;

        public IReadOnlyList<int> SnapshotSessionProcessIds(string imageName) =>
            Snapshots.Count > 0 ? Snapshots.Dequeue() : SteadySnapshot;

        public bool Start(string fileName, IReadOnlyList<string>? arguments, bool useShellExecute = true)
        {
            Starts.Add((fileName, arguments, useShellExecute));
            if (StartResult && LaunchMakesRunning)
            {
                SimulateLaunchedProcessSurvives();
            }

            return StartResult;
        }
    }

    /// <summary>一切读取都失败的后端（测 fail-closed）。</summary>
    private sealed class ThrowingReadBackend : IRegistryBackend
    {
        public RegistryValue? ReadValue(string subKey, string name) =>
            throw new RegistryReadException("模拟读失败");

        public RegistryValue? ReadHklmValue(string subKey, string name) =>
            throw new RegistryReadException("模拟读失败");

        public void WriteValue(string subKey, string name, object data, Microsoft.Win32.RegistryValueKind kind)
        {
        }

        public void DeleteValue(string subKey, string name)
        {
        }

        public IReadOnlyDictionary<string, RegistryValue> EnumerateValues(string subKey) =>
            new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
    }
}
