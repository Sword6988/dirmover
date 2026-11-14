using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DirMover.App.ViewModels;
using DirMover.Core;
using DirMover.Core.Diagnostics;
using DirMover.Core.Engine;
using DirMover.Core.IO;
using DirMover.Core.Registry;
using DirMover.Core.Shell;
using DirMover.Wpf.Dialogs;

namespace DirMover.Wpf;

/// <summary>
/// 应用宿主：启动编排、命令行解析、单实例、全局异常兜底。
/// </summary>
/// <remarks>
/// 与 WinUI 版 App.xaml.cs 一一对应，差异只在"宿主 API"：
/// <c>OnLaunched</c> → <c>OnStartup</c>；<c>ContentDialog</c> → <c>DialogWindow</c>；
/// <c>UnhandledException</c> → <c>DispatcherUnhandledException</c>。
/// 退出码约定（README 已承诺给调用方）：0 正常 / 2 环境不满足 / 3 界面初始化失败 / 4 未预期错误。
/// </remarks>
public partial class App : Application
{
    /// <summary>退出码：正常。</summary>
    internal const int ExitOk = 0;

    /// <summary>退出码：环境不满足（无可用目标盘 / 未知参数）。</summary>
    internal const int ExitEnvironment = 2;

    /// <summary>退出码：界面初始化失败。</summary>
    internal const int ExitUiInitFailed = 3;

    /// <summary>退出码：未预期错误。</summary>
    internal const int ExitUnexpected = 4;

    /// <summary>未处理异常次数上限：达到后不再"继续运行"，直接以 <see cref="ExitUnexpected"/> 退出。</summary>
    private const int MaxUnhandledExceptions = 2;

    private IAppPaths _paths = null!;
    private IAppLog _log = NullLog.Instance;
    private ParsedArgs _args = ParsedArgs.None;
    private MainWindow? _window;
    private SingleInstanceGuard? _guard;
    private bool _cliStarted;
    private int _unhandledCount;

    // 启动耗时分解（用户反馈"启动慢"后加的仪表）：Stopwatch 从 App 构造起跑，
    // 各里程碑在 ContentRendered 时一次性落一行 INFO，不逐段刷日志。
    private readonly System.Diagnostics.Stopwatch _boot = System.Diagnostics.Stopwatch.StartNew();
    private readonly double _processStartToAppCtorMs = ComputeProcessStartDeltaMs();
    private long _tOnStartup, _tWindowCtor, _tShown;

    /// <summary>进程启动到 App 构造的毫秒差（近似 CLR 初始化 + 主程序集加载的成本）。</summary>
    private static double ComputeProcessStartDeltaMs()
    {
        try
        {
            return (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>构造应用实例，加载资源字典。</summary>
    /// <remarks>
    /// WPF 侧<b>没有</b>运行期绑定失败诊断：WinUI 的 <c>DebugSettings.BindingFailed</c> 在 WPF
    /// 没有等价物，而 <c>PresentationTraceSources</c> 的开关必须在 app.config 里声明才真正生效
    /// （实测：构造期设 <c>Switch.Level = All</c> + 挂 listener，仍然一条事件都收不到）。
    /// 单文件承诺禁止 exe.config，所以改用确定性的静态校验：<c>tools/check-bindings.py</c>
    /// 解析 XAML 里每个绑定路径并与 C# 属性表对照，路径写错在提交前就报错。
    /// </remarks>
    public App()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        InitializeComponent();
        AppCtorInitMs = sw.ElapsedMilliseconds;
    }

    /// <summary>App.xaml（合并资源字典）解析耗时；由启动分解日志消费。</summary>
    internal long AppCtorInitMs { get; private set; }

    /// <summary>磁盘枚举耗时；由启动分解日志消费。</summary>
    internal long DriveEnumMs { get; private set; }

    /// <summary>MainViewModel 构造耗时；由启动分解日志消费。</summary>
    internal long ViewModelCtorMs { get; private set; }

    /// <summary>Attach（DataContext 赋值）耗时；由启动分解日志消费。</summary>
    internal long AttachMs { get; private set; }

    /// <summary>Show() 调用耗时；由启动分解日志消费。</summary>
    internal long ShowMs { get; private set; }

    /// <summary>启动编排。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _tOnStartup = _boot.ElapsedMilliseconds;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteStartupError(args.ExceptionObject as Exception);

        try
        {
            _paths = DefaultAppPaths.Resolve(AppContext.BaseDirectory);
            _log = FileLogger.Create(_paths);
            _args = ParsedArgs.Parse(Environment.GetCommandLineArgs());
            _log.Info($"启动：{string.Join(" ", _args.Raw)}");
        }
        catch (Exception ex)
        {
            // 连落点都算不出来（极少见，例如目录被删）：只能退回临时目录写一行。
            WriteStartupError(ex);
            Shutdown(ExitUnexpected);
            return;
        }

        if (!TryCreateWindow())
        {
            Shutdown(ExitUiInitFailed);
        }
    }

    private bool TryCreateWindow()
    {
        try
        {
            _window = new MainWindow();
            _tWindowCtor = _boot.ElapsedMilliseconds;

            long tDrives0 = _boot.ElapsedMilliseconds;
            DriveVolume[] drives = DriveInventory.ListFixedNonSystem().ToArray();
            long tDrives1 = _boot.ElapsedMilliseconds;
            if (drives.Length == 0)
            {
                _log.Warn("未检测到可用的非系统固定磁盘");
            }

            var store = new RegistryStore(new Win32RegistryBackend(), _log);
            var engine = new TransferEngine(store, _paths.BackupDirectory, _args.DryRun, _log);
            var dialogs = new WpfDialogService(_window, _paths, _log);
            var viewModel = new MainViewModel(engine, _paths, _log, dialogs, drives);
            long tViewModel1 = _boot.ElapsedMilliseconds;
            DriveEnumMs = tDrives1 - tDrives0;
            ViewModelCtorMs = tViewModel1 - tDrives1;

            viewModel.ReleaseSingleInstanceGate = ReleaseSingleInstance;
            viewModel.ReacquireSingleInstanceGate = ReacquireSingleInstance;

            long tAttach0 = _boot.ElapsedMilliseconds;
            _window.Attach(viewModel);
            long tAttach1 = _boot.ElapsedMilliseconds;
            _window.Show();
            _tShown = _boot.ElapsedMilliseconds;
            AttachMs = tAttach1 - tAttach0;
            ShowMs = _tShown - tAttach1;

            // 启动分解只在首帧后落一次日志；独立订阅，CLI / 试运行路径也覆盖。
            _window.ContentRendered += (_, _) => LogBootBreakdown();

            // 单实例判定放在窗口显示之后：重复启动要能"弹一句再走"，而弹窗需要 Owner。
            _guard = SingleInstanceGuard.Acquire(_paths, _log, out bool alreadyRunning);
            if (alreadyRunning)
            {
                _ = ShowAlreadyRunningAsync(dialogs);
                return true;
            }

            if (_args.IsCliOnly)
            {
                // 用 ContentRendered 而不是 Loaded：WPF 的 Window.Loaded 在"Show 之后才订阅"
                // 的时序下实测不触发（WinUI 的 Window.Loaded 语义是渲染完成），
                // ContentRendered 在首帧绘制后触发，弹窗所需的宿主状态此时一定就绪。
                // 订阅后立刻退订：该事件在窗口被还原/重绘时可能再次触发，而 CLI 只能跑一次。
                EventHandler? onRendered = null;
                onRendered = async (_, _) =>
                {
                    _window.ContentRendered -= onRendered;
                    await RunCliAsync(dialogs, store);
                };
                _window.ContentRendered += onRendered;
            }
            else if (!_args.DryRun)
            {
                // 启动期遗留事务检查：等窗口先画出来再查（回滚会写注册表，试运行跳过）。
                EventHandler? onRendered = null;
                onRendered = async (_, _) =>
                {
                    _window.ContentRendered -= onRendered;

                    try
                    {
                        await Task.Delay(Catalog.PendingIntentDelay);
                        await viewModel.CheckPendingIntentsAsync();
                    }
                    catch (Exception ex)
                    {
                        // 启动期检查失败不致命（下次启动还会提示），但必须可见。
                        _log.Error("启动期遗留事务检查失败", ex);
                    }
                };
                _window.ContentRendered += onRendered;
            }

            return true;
        }
        catch (Exception ex)
        {
            WriteStartupError(ex);
            _log.Error("窗口创建失败", ex);
            return false;
        }
    }

    private async Task ShowAlreadyRunningAsync(IDialogService dialogs)
    {
        await dialogs.WarningAlreadyRunningAsync();

        // 重复启动是正常路径，不是错误。
        Shutdown(ExitOk);
    }

    /// <summary>只读 CLI 路径：<c>--scan</c> / <c>--version</c> / <c>--help</c> / 未知参数。</summary>
    private async Task RunCliAsync(IDialogService dialogs, RegistryStore store)
    {
        if (_cliStarted)
        {
            return;
        }

        _cliStarted = true;

        try
        {
            if (_args.Scan)
            {
                await RunScanAsync(dialogs, store);
                ExitAfterCli(ExitOk);
                return;
            }

            if (_args.ShowVersion || _args.ShowHelp || _args.Unknown.Count > 0)
            {
                string title = _args.Unknown.Count > 0 ? UiText.DialogCannotStart : UiText.WindowTitle;
                await dialogs.ShowTextAsync(title, BuildHelpText(_args.Unknown), showOpenLogButton: true);
                ExitAfterCli(_args.Unknown.Count > 0 ? ExitEnvironment : ExitOk);
            }
        }
        catch (Exception ex)
        {
            _log.Error("CLI 路径失败", ex);
            WriteStartupError(ex);
            ExitAfterCli(ExitUnexpected);
        }
    }

    /// <summary>
    /// 只读 CLI 跑完的收尾：落退出码 + 关掉宿主窗口，让进程真的结束。
    /// </summary>
    /// <param name="exitCode">约定的退出码。</param>
    /// <remarks>
    /// 只设 <see cref="Environment.ExitCode"/> 是不够的：主窗口还开着，
    /// <see cref="Application.Run"/> 就不会返回，<c>DirMover.exe --version</c> 会一直挂着
    /// （2026-11-14 实测：关掉弹窗后进程仍是 <c>STILL-RUNNING</c>，主窗口 980×760 还在）。
    /// 命令行参数的价值恰恰在"能放进脚本、跑完就退"，所以这里必须把窗口关掉。
    /// </remarks>
    private void ExitAfterCli(int exitCode)
    {
        Environment.ExitCode = exitCode;
        _window?.CloseForCliExit();
    }

    private async Task RunScanAsync(IDialogService dialogs, RegistryStore store)
    {
        ScanReportResult report = await ScanReport.BuildAsync(store);

        string path = _paths.ScanFileFor(DateTimeOffset.Now);
        if (!report.WriteToFile(path))
        {
            _log.Warn("扫描报告未能写入文件");
        }
        else
        {
            _log.Info($"扫描报告已写入：{path}");
        }

        if (report.Warnings.Count > 0)
        {
            foreach (string warning in report.Warnings)
            {
                _log.Warn(warning);
            }
        }

        await dialogs.ShowTextAsync(UiText.DialogScanReportTitle, report.ToText(), showOpenLogButton: true);
    }

    /// <summary>
    /// <c>--version</c> / <c>--help</c> 的正文（与 WinUI 版同一份文案，保持两版输出一致）。
    /// </summary>
    private static string BuildHelpText(IReadOnlyList<string> unknown)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"{UiText.WindowTitle}  {Catalog.AppVersion}");
        builder.AppendLine();
        builder.AppendLine("Usage: DirMover.exe [option]");
        builder.AppendLine("  (no option)   normal GUI");
        builder.AppendLine("  --dry-run     GUI in dry-run mode: no registry / file changes");
        builder.AppendLine("  --scan        read-only scan, writes a report under logs\\ and shows it");
        builder.AppendLine("  --version     show version");
        builder.AppendLine("  --help        show this help");

        if (unknown.Count > 0)
        {
            builder.AppendLine();
            builder.Append("Unknown argument(s): ").Append(string.Join(", ", unknown));
        }

        return builder.ToString();
    }

    /// <summary>把启动里程碑落成一行日志：各段毫秒数，定位启动慢的环节。</summary>
    private void LogBootBreakdown()
    {
        try
        {
            long now = _boot.ElapsedMilliseconds;
            _log.Info(
                "启动分解: " +
                $"进程→App构造={_processStartToAppCtorMs:F0}ms " +
                $"App构造→OnStartup={_tOnStartup}ms " +
                $"(App.xaml解析={AppCtorInitMs}ms) " +
                $"OnStartup→窗口构造={_tWindowCtor - _tOnStartup}ms " +
                $"(MainWindow.xaml解析={DirMover.Wpf.MainWindow.CtorInitMs}ms) " +
                $"窗口→Show={_tShown - _tWindowCtor}ms " +
                $"(盘枚举={DriveEnumMs}ms VM构造={ViewModelCtorMs}ms Attach={AttachMs}ms Show={ShowMs}ms 列测量={DirMover.Wpf.MainWindow.ColumnMeasureMs}ms) " +
                $"Show→首帧={now - _tShown}ms " +
                $"合计(App构造起)={now}ms");
        }
        catch
        {
            // 仪表自身故障不报警：启动路径不受影响。
        }
    }

    /// <summary>未处理异常：第一次告知并继续，第二次以退出码 4 收场。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        int ordinal = Interlocked.Increment(ref _unhandledCount);

        WriteStartupError(e.Exception);
        _log.Error("未处理异常", e.Exception);

        // 与 WinUI 版同一条口径：出现未处理异常即为"未预期错误"，进程最终退出码必须是 4。
        // 只在 Shutdown 那条分支设的话，"第一次异常后继续用、最后正常关窗"会退出 0，
        // 那条承诺（README：4 = 未预期错误）就落空了。
        Environment.ExitCode = ExitUnexpected;

        if (ordinal >= MaxUnhandledExceptions)
        {
            // 进程活着但界面已经坏了是更糟的状态：没有提示、点哪都没反应。
            e.Handled = true;
            Shutdown(ExitUnexpected);
            return;
        }

        e.Handled = true;

        if (_window is not null)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => _ = ShowUnexpectedErrorDialogAsync(_window)));
        }
    }

    private static async Task ShowUnexpectedErrorDialogAsync(MainWindow window)
    {
        try
        {
            var host = DialogLayout.CreateBody(UiText.UnexpectedErrorBody);
            var dialog = new DialogWindow { Owner = window };
            dialog.Configure(UiText.DialogUnexpectedError, host, null, null, UiText.ButtonOk, DialogLayout.PreferredWidth());
            dialog.ShowDialog();
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            // 崩溃告知本身失败不能再抛：此刻"未处理异常"才是主事件。
            System.Diagnostics.Debug.WriteLine($"unexpected-error dialog failed: {ex.Message}");
        }
    }

    private void WriteStartupError(Exception? ex)
    {        if (ex is null)
        {
            return;
        }

        // _paths 为 null 意味着"连落点都没算出来"（DefaultAppPaths.Resolve 抛了）——
        // 这个兜底恰恰要在那种时候工作，所以不能直接解引用。走临时目录。
        IAppPaths? paths = _paths;
        string target;
        try
        {
            target = paths is null
                ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DirMover-startup_error.txt")
                : paths.StartupErrorFile;

            string? directory = System.IO.Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllText(
                target,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}");
        }
        catch (Exception inner)
        {
            System.Diagnostics.Debug.WriteLine($"startup_error write failed: {inner.Message}");
        }
    }

    private void ReleaseSingleInstance()
    {
        _guard?.Release();
        _guard = null;
    }    private void ReacquireSingleInstance()
    {
        _guard = SingleInstanceGuard.Acquire(_paths, _log, out _);
    }

    /// <summary>命令行参数（大小写不敏感，来源是 <see cref="Environment.GetCommandLineArgs"/>）。</summary>
    internal sealed class ParsedArgs
    {
        internal static readonly ParsedArgs None = new();

        private ParsedArgs()
        {
            Raw = Array.Empty<string>();
            Unknown = Array.Empty<string>();
        }

        private ParsedArgs(bool dryRun, bool scan, bool version, bool help, List<string> unknown, string[] raw)
        {
            DryRun = dryRun;
            Scan = scan;
            ShowVersion = version;
            ShowHelp = help;
            Unknown = unknown;
            Raw = raw;
        }

        /// <summary>试运行：零副作用。</summary>
        internal bool DryRun { get; }

        /// <summary>只读扫描。</summary>
        internal bool Scan { get; }

        /// <summary>显示版本。</summary>
        internal bool ShowVersion { get; }

        /// <summary>显示帮助。</summary>
        internal bool ShowHelp { get; }

        /// <summary>未识别的参数（非空时按 HELP 处理，退出码 2）。</summary>
        internal IReadOnlyList<string> Unknown { get; }

        /// <summary>原始参数（写日志用）。</summary>
        internal IReadOnlyList<string> Raw { get; }

        /// <summary>是否只走只读 CLI 路径。</summary>
        internal bool IsCliOnly => Scan || ShowVersion || ShowHelp || Unknown.Count > 0;

        /// <summary>解析命令行（<c>--scan</c> 优先于 <c>--dry-run</c>）。</summary>
        internal static ParsedArgs Parse(string[] argv)
        {
            if (argv is null || argv.Length <= 1)
            {
                return new ParsedArgs(false, false, false, false, new List<string>(), Array.Empty<string>());
            }

            var raw = new string[argv.Length - 1];
            Array.Copy(argv, 1, raw, 0, raw.Length);

            bool dryRun = false;
            bool scan = false;
            bool version = false;
            bool help = false;
            var unknown = new List<string>();

            foreach (string argument in raw)
            {
                switch (argument.ToLowerInvariant())
                {
                    case "--dry-run":
                    case "--dryrun":
                        dryRun = true;
                        break;
                    case "--scan":
                        scan = true;
                        break;
                    case "--version":
                    case "-v":
                        version = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        help = true;
                        break;
                    default:
                        unknown.Add(argument);
                        break;
                }
            }

            return new ParsedArgs(dryRun, scan, version, help, unknown, raw);
        }
    }
}
