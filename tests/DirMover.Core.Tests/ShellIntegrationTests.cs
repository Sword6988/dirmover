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
        executor.RunningResults.Enqueue(true); // 静置后复查：已回来 → 无需拉起
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.True(result.Ok);
        (string file, IReadOnlyList<string> arguments) = Assert.Single(executor.Runs);
        Assert.Equal("taskkill.exe", file);
        Assert.Contains("/im", arguments);
        Assert.Contains("explorer.exe", arguments);
        Assert.Contains("/fi", arguments);
        Assert.Contains(arguments, argument => argument.StartsWith("username eq", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(arguments, argument => argument.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestartExplorer_ShellLaunchSucceeds_NoFallbackUsed()
    {
        var executor = new FakeProcessExecutor();
        executor.RunningResults.Enqueue(false); // 没起来 → 拉起
        executor.RunningResults.Enqueue(true); // 存活确认
        using var scope = ShellTestScope.Create(executor);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.True(result.Ok);
        Assert.Single(scope.LaunchAttempts);
        Assert.Empty(executor.Starts);
    }

    [Fact]
    public async Task RestartExplorer_ShellFails_FallsBackToProcessStart()
    {
        var executor = new FakeProcessExecutor { StartResult = true };
        executor.RunningResults.Enqueue(false); // 没起来 → 拉起
        executor.RunningResults.Enqueue(true); // 存活确认
        using var scope = ShellTestScope.Create(executor, shellLaunchResult: false);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.True(result.Ok);
        (string file, _) = Assert.Single(executor.Starts);
        Assert.EndsWith("explorer.exe", file, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestartExplorer_ShellAndFallbackFail_SaysRunManually()
    {
        var executor = new FakeProcessExecutor { StartResult = false };
        executor.RunningResults.Enqueue(false);
        using var scope = ShellTestScope.Create(executor, shellLaunchResult: false);

        ShellOpResult result = await ShellIntegration.RestartExplorerAsync();

        Assert.False(result.Ok);
        Assert.Contains("请手动运行 explorer.exe", result.Message);
        Assert.Single(executor.Starts);
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

    /// <summary>替换进程执行器与 shell 启动钩子，用例结束后恢复。</summary>
    private sealed class ShellTestScope : IDisposable
    {
        private readonly IProcessExecutor _originalExecutor;
        private readonly Func<string?, string, string?, bool>? _originalHook;

        private ShellTestScope(IProcessExecutor executor, bool? shellLaunchResult)
        {
            _originalExecutor = ShellIntegration.Executor;
            _originalHook = ShellIntegration.ShellLaunchHook;
            ShellIntegration.Executor = executor;

            if (shellLaunchResult is null)
            {
                ShellIntegration.ShellLaunchHook = (verb, file, arguments) =>
                {
                    LaunchAttempts.Add((verb, file, arguments));
                    return true;
                };
            }
            else
            {
                bool success = shellLaunchResult.Value;
                ShellIntegration.ShellLaunchHook = (verb, file, arguments) =>
                {
                    LaunchAttempts.Add((verb, file, arguments));
                    return success;
                };
            }
        }

        internal List<(string? Verb, string File, string? Arguments)> LaunchAttempts { get; } = new();

        internal static ShellTestScope Create(IProcessExecutor executor, bool? shellLaunchResult = null) =>
            new(executor, shellLaunchResult);

        public void Dispose()
        {
            ShellIntegration.Executor = _originalExecutor;
            ShellIntegration.ShellLaunchHook = _originalHook;
        }
    }

    /// <summary>记录参数、不真的启动任何进程。</summary>
    private sealed class FakeProcessExecutor : IProcessExecutor
    {
        internal List<(string File, IReadOnlyList<string> Arguments)> Runs { get; } = new();

        internal List<(string File, IReadOnlyList<string>? Arguments)> Starts { get; } = new();

        internal Queue<bool> RunningResults { get; } = new();

        internal bool StartResult { get; set; } = true;

        public int Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Runs.Add((fileName, arguments));
            return 0;
        }

        public bool IsRunning(string imageName) => RunningResults.Count > 0 && RunningResults.Dequeue();

        public bool Start(string fileName, IReadOnlyList<string>? arguments)
        {
            Starts.Add((fileName, arguments));
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
