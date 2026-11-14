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

    /// <summary>启动编排。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

            DriveVolume[] drives = DriveInventory.ListFixedNonSystem().ToArray();
            if (drives.Length == 0)
            {
                _log.Warn("未检测到可用的非系统固定磁盘");
            }

            var store = new RegistryStore(new Win32RegistryBackend(), _log);
            var engine = new TransferEngine(store, _paths.BackupDirectory, _args.DryRun, _log);
            var dialogs = new WpfDialogService(_window, _paths, _log);
            var viewModel = new MainViewModel(engine, _paths, _log, dialogs, drives);

            viewModel.ReleaseSingleInstanceGate = ReleaseSingleInstance;
            viewModel.ReacquireSingleInstanceGate = ReacquireSingleInstance;

            _window.Attach(viewModel);
            _window.Show();

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
                _window.ContentRendered += async (_, _) => await RunCliAsync(dialogs, store);
            }
            else if (!_args.DryRun)
            {
                // 启动期遗留事务检查：等窗口先画出来再查（回滚会写注册表，试运行跳过）。
                _window.ContentRendered += async (_, _) =>
                {
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
                Environment.ExitCode = ExitOk;
                return;
            }

            if (_args.ShowVersion || _args.ShowHelp || _args.Unknown.Count > 0)
            {
                string title = _args.Unknown.Count > 0 ? UiText.DialogCannotStart : UiText.WindowTitle;
                await dialogs.ShowTextAsync(title, BuildHelpText(_args.Unknown), showOpenLogButton: true);
                Environment.ExitCode = _args.Unknown.Count > 0 ? ExitEnvironment : ExitOk;
            }
        }
        catch (Exception ex)
        {
            _log.Error("CLI 路径失败", ex);
            WriteStartupError(ex);
            Environment.ExitCode = ExitUnexpected;
        }
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

    /// <summary>未处理异常：第一次告知并继续，第二次以退出码 4 收场。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        int ordinal = Interlocked.Increment(ref _unhandledCount);

        WriteStartupError(e.Exception);
        _log.Error("未处理异常", e.Exception);

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
    {
        if (ex is null)
        {
            return;
        }

        try
        {
            System.IO.File.WriteAllText(
                _paths.StartupErrorFile,
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
    }

    private void ReacquireSingleInstance()
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
