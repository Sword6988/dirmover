using DirMover.App.Dialogs;
using DirMover.App.ViewModels;
using DirMover.Core;
using DirMover.Core.Diagnostics;
using DirMover.Core.Engine;
using DirMover.Core.IO;
using DirMover.Core.Registry;
using DirMover.Core.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DirMover.App;

/// <summary>
/// 应用宿主：启动编排、命令行解析、单实例、全局异常兜底（架构 §7.7）。
/// </summary>
/// <remarks>
/// 所有参数解析都集中在 <see cref="ParsedArgs"/> 一个类型里，<b>不</b>散落到视图模型：
/// 视图模型一旦认识 <c>--dry-run</c> 这类字面量，"试运行"就会从"一个开关"变成"每个命令里一处 if"。
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

    /// <summary>
    /// 未处理异常的次数上限：达到这个次数就不再"继续运行"，直接以 <see cref="ExitUnexpected"/> 退出。
    /// </summary>
    private const int MaxUnhandledExceptions = 2;

    private IAppPaths _paths = null!;
    private IAppLog _log = NullLog.Instance;
    private ParsedArgs _args = ParsedArgs.None;
    private MainWindow? _window;
    private SingleInstanceGuard? _guard;
    private bool _cliStarted;

    /// <summary>本进程已发生的未处理异常次数（第一次告知并继续，第二次直接退出）。</summary>
    private int _unhandledCount;

    /// <summary>构造应用实例，加载资源字典，并挂上全局异常与绑定失败诊断。</summary>
    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) => HandleUnhandledException(e);

        // {Binding} 是运行期解析：路径拼错不报编译错、也不抛异常，只是永远不生效。
        // BindingFailed 是唯一能把这类"静默故障"变成可见日志的手段，故常开（不弹窗）。
        DebugSettings.IsBindingTracingEnabled = true;
        DebugSettings.BindingFailed += (_, e) => ReportBindingFailure(e.Message);
    }

    /// <summary>启动编排。</summary>
    /// <param name="args">启动参数。</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _paths = DefaultAppPaths.Resolve(AppContext.BaseDirectory);
            _log = FileLogger.Create(_paths);
            _args = ParsedArgs.Parse(Environment.GetCommandLineArgs());
            _log.Info($"启动：{string.Join(' ', _args.Raw)}");
        }
        catch (Exception ex)
        {
            // 连落点都算不出来（极少见，例如目录被删）：只能退回临时目录写一行。
            WriteStartupError(ex);
            Environment.ExitCode = ExitUnexpected;
            return;
        }

        if (!TryCreateWindow())
        {
            Environment.ExitCode = ExitUiInitFailed;
            return;
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
            var dialogs = new DialogService(_window, _paths, _log);
            var viewModel = new MainViewModel(engine, _paths, _log, dialogs, drives);
            viewModel.ReleaseSingleInstanceGate = ReleaseSingleInstance;
            viewModel.ReacquireSingleInstanceGate = ReacquireSingleInstance;

            _window.Attach(viewModel);

            // 先关交互再激活：单实例判定必须在 Activate 之后（重复启动要弹窗，
            // 弹窗需要 XamlRoot，也就需要已激活的窗口），而 Activate 到判定之间有一段窗口期。
            // 不关的话，第二个实例的主界面会先正常亮出来、主按钮可用，用户在这段时间里
            // 点"开始"就是两个实例并发搬运同一批目录 —— 那是会真损坏数据的。
            // 开关落在视图模型上（窗口内容是 Grid，没有 IsEnabled；IsHitTestVisible 也挡不住键盘）。
            viewModel.InteractionEnabled = false;

            _window.Activate();

            // 单实例判定放在窗口创建之后：重复启动要能"弹一句再走"，
            // 而弹窗必须有 XamlRoot，也就必须先有已激活的窗口。
            _guard = SingleInstanceGuard.Acquire(_paths, _log, out bool alreadyRunning);
            if (alreadyRunning)
            {
                // 重复实例：交互保持关闭，弹完提示后由 ShowAlreadyRunningAsync 自己退出。
                _ = ShowAlreadyRunningAsync(dialogs);
                return true;
            }

            // 本实例是唯一实例，放开交互。
            viewModel.InteractionEnabled = true;

            if (_args.IsCliOnly && _window.Content is FrameworkElement cliRoot)
            {
                cliRoot.Loaded += async (_, _) => await RunCliAsync(dialogs, store);
            }
            else if (_window.Content is FrameworkElement root && !_args.DryRun)
            {
                // 启动期遗留事务检查（架构 §4.4 时序图 ③、§8-R5、T14-⑥）：
                // 窗口 Loaded 之后等 PendingIntentDelay（让窗口先画出来）再检查。
                // 试运行模式跳过——--dry-run 承诺零副作用，而「回滚」会写注册表；
                // 记录保留，下次正常启动时再提示。
                root.Loaded += async (_, _) =>
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

        // 重复启动是正常路径，不是错误（架构 §7.7 退出码判定点 ①）。
        Environment.ExitCode = ExitOk;
    }

    /// <summary>
    /// 只读 CLI 路径：<c>--scan</c> / <c>--version</c> / <c>--help</c> / 未知参数。
    /// </summary>
    /// <remarks>
    /// 三者共用同一个只读文本弹窗，<b>不新增页面</b>（裁决 Q1）。等窗口 <c>Loaded</c> 之后才跑，
    /// 是因为 <see cref="DialogService"/> 需要 <c>XamlRoot</c>，否则弹窗会被静默跳过。
    /// </remarks>
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
            // 写不进磁盘不影响"让用户看到报告"这件事：弹窗照常，只记一条告警。
            _log.Warn("扫描报告未能写入文件");
        }
        else
        {
            _log.Info($"扫描报告已写入：{path}");
        }

        if (report.Warnings.Count > 0)
        {
            // 告警是给用户看的，不是吞掉的理由，但仍照常展示报告全文。
            foreach (string warning in report.Warnings)
            {
                _log.Warn(warning);
            }
        }

        await dialogs.ShowTextAsync(UiText.DialogScanReportTitle, report.ToText(), showOpenLogButton: true);
    }

    /// <summary>
    /// <c>--version</c> / <c>--help</c> 的正文。
    /// </summary>
    /// <param name="unknown">未识别的参数。</param>
    /// <remarks>
    /// 目前全部为 ASCII。原因是 <see cref="UiText"/> 里还没有帮助文案常量，而"文案一律取自 UiText"
    /// 是项目红线，不能在这里临时写中文。补齐方式见交付说明：需要在 Core 的 <c>UiText</c>
    /// 增加 <c>HelpText</c> / <c>UnknownArgument</c> 两个成员，届时本方法直接改为转调。
    /// </remarks>
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

    private void ReportBindingFailure(string message)
    {
        // 两路都要：Debug.WriteLine 给附加调试器的人，文件给"用户只拿到了一个 exe"的场景。
        System.Diagnostics.Debug.WriteLine($"BindingFailed: {message}");
        _log.Warn($"绑定失败：{message}");

        try
        {
            System.IO.Directory.CreateDirectory(_paths.LogDirectory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(_paths.LogDirectory, "binding-failed.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}\t{message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // 诊断通道本身失败不能再抛：它是在"绑定已经坏了"的时候才被调用的。
            System.Diagnostics.Debug.WriteLine($"BindingFailed-log-write-failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 未处理异常的统一处置（架构 §7.7、README 退出码 4）。
    /// </summary>
    /// <param name="e">异常参数；<c>Handled</c> 决定本次是继续运行还是就此收场。</param>
    /// <remarks>
    /// 契约（交付总监裁决）：
    /// <para>
    /// ① 同一进程第 <b>1</b> 次：写启动错误文件（<see cref="WriteStartupError"/>）→ 尝试弹一次
    /// 告知性弹窗 → <c>Handled = true</c> 继续运行。直接退出会把"某个按钮的回调抛了异常"
    /// 放大成"整个程序消失"，用户已做的勾选与目标盘选择全部白费。
    /// </para>
    /// <para>
    /// ② 同一进程第 <b>2</b> 次：<see cref="ExitUnexpected"/> 退出。一直 <c>Handled = true</c>
    /// 会得到"进程活着但界面已经坏了"的僵尸状态——用户点任何东西都没反应、也没有任何提示，
    /// 而 README 把退出码 4 承诺给了调用方，那条承诺必须真的可达（原实现里它永远不可达）。
    /// </para>
    /// </remarks>
    private void HandleUnhandledException(Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        int ordinal = Interlocked.Increment(ref _unhandledCount);

        // 先落盘：无论随后是继续还是退出，"有痕迹"这一条永远成立。
        WriteStartupError(e.Exception);
        Environment.ExitCode = ExitUnexpected;

        if (ordinal >= MaxUnhandledExceptions)
        {
            // 不再尝试任何 UI：界面可能已经坏到弹不出窗，弹窗失败也只是再添一条噪音。
            ExitImmediately();
            return;
        }

        e.Handled = true;
        NotifyUnexpectedErrorOnce();
    }

    /// <summary>
    /// 把"弹一次告知性弹窗"排到 UI 线程；排不上就静默降级成"只看日志"。
    /// </summary>
    /// <remarks>
    /// 三条硬要求：① 不得在后台线程直接 new <c>ContentDialog</c>（WinUI 会在错误的线程上
    /// 抛 <c>RPC_E_WRONG_THREAD</c>）；② 弹窗失败不得再抛（本方法只在异常兜底路径上被调用）；
    /// ③ <c>ContentDialog</c> 必须有 <c>XamlRoot</c>，窗口/内容树未就绪时只能放弃弹窗。
    /// </remarks>
    private void NotifyUnexpectedErrorOnce()
    {
        MainWindow? window = _window;
        if (window is null)
        {
            // 窗口还没建出来（异常发生在 App 构造 / OnLaunched 早期）：没有 XamlRoot，只剩日志。
            return;
        }

        try
        {
            // TryEnqueue 在队列已关闭（正在退出）时返回 false → 静默降级。
            _ = window.DispatcherQueue.TryEnqueue(() => _ = ShowUnexpectedErrorDialogAsync(window));
        }
        catch (Exception ex)
        {
            // 排队本身失败不能再抛：此刻"未处理异常"才是主事件，诊断出口失败只留调试痕迹。
            System.Diagnostics.Debug.WriteLine($"unexpected-error dispatch failed: {ex.Message}");
        }
    }

    /// <summary>弹一次告知性弹窗（文案全部取自 <see cref="UiText"/>）。</summary>
    /// <param name="window">主窗口（用于取 <c>XamlRoot</c>）。</param>
    private static async Task ShowUnexpectedErrorDialogAsync(MainWindow window)
    {
        try
        {
            XamlRoot? root = window.Content?.XamlRoot;
            if (root is null)
            {
                // 内容树未就绪：静默降级，用户仍能从启动错误文件与日志看到痕迹。
                return;
            }

            // 与业务弹窗共用同一套排版（段落化正文 + 窗口尺寸自适应）：
            // 崩溃告知是用户唯一能看到的诊断出口，正文必须完整、可读、可复制。
            var host = DialogLayout.CreateBody(UiText.UnexpectedErrorBody);
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = UiText.DialogUnexpectedError,
                Content = host,
                CloseButtonText = UiText.ButtonOk,
            };

            DialogLayout.FitToWindow(host, root.Size);

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // 已有弹窗在显示 / 内容树已卸载：静默降级，绝不从兜底路径再抛一次。
            System.Diagnostics.Debug.WriteLine($"unexpected-error dialog failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 以 <see cref="ExitUnexpected"/> 立即结束进程。
    /// </summary>
    /// <remarks>
    /// 单独抽一个方法只是为了不让 <see cref="Environment.Exit"/> 的 <c>[DoesNotReturn]</c> 注解
    /// 把调用点之后的代码标成"不可达"（那会引入 CS0162 警告，破坏 0 警告门禁）。
    /// </remarks>
    private static void ExitImmediately() => Environment.Exit(ExitUnexpected);

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

    /// <summary>
    /// 提权重启前释放单实例权（架构 §8-R7）：Release 并删 PID 文件，
    /// 让新提权实例能正常获得 <see cref="SingleInstanceGuard"/>。
    /// </summary>
    private void ReleaseSingleInstance()
    {
        _guard?.Release();
        _guard = null;
    }

    /// <summary>提权重启失败（含 UAC 被取消）后恢复单实例权，让本实例继续受单实例保护。</summary>
    private void ReacquireSingleInstance()
    {
        _guard = SingleInstanceGuard.Acquire(_paths, _log, out _);
    }

    /// <summary>
    /// 命令行参数。大小写不敏感，来源是 <see cref="Environment.GetCommandLineArgs"/>。
    /// </summary>
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

        /// <summary>原始参数（写日志用，便于复现）。</summary>
        internal IReadOnlyList<string> Raw { get; }

        /// <summary>
        /// 是否只走只读 CLI 路径（不进入主流程状态机）。
        /// </summary>
        /// <remarks>
        /// <c>--scan</c> 与 <c>--dry-run</c> 同时给时以 <c>--scan</c> 优先：
        /// 扫描报告内部恒定以 <c>dryRun: true</c> 构造引擎，报告内容与 <c>--dry-run</c> 无关，
        /// 两者不冲突，取更"只读"的那个即可。
        /// </remarks>
        internal bool IsCliOnly => Scan || ShowVersion || ShowHelp || Unknown.Count > 0;

        /// <summary>解析命令行。</summary>
        /// <param name="argv">完整命令行（含 exe 自身，会被跳过）。</param>
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
