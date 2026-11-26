using System.Collections.ObjectModel;
using System.ComponentModel;
using DirMover.App.Views;
using DirMover.Core;
using DirMover.Core.Diagnostics;
using DirMover.Core.Engine;
using DirMover.Core.IO;
using DirMover.Core.Registry;
using DirMover.Core.Shell;
using Microsoft.UI.Dispatching;

namespace DirMover.App.ViewModels;

/// <summary>未完成事务的处置选择（架构 §7.5 三选弹窗）。</summary>
public enum PendingChoice
{
    /// <summary>回滚到转移前。</summary>
    Rollback,

    /// <summary>忽略记录，继续使用现状。</summary>
    Ignore,

    /// <summary>不处置（关窗）。</summary>
    Keep,
}

/// <summary>
/// 弹窗出口（架构 §7.5 的 14 类业务弹窗 + 1 个只读文本弹窗）。
/// </summary>
/// <remarks>
/// 架构原文给的是具体类 <c>DialogService</c>。这里额外抽一层窄接口，原因是：
/// <list type="bullet">
/// <item><c>MainViewModel</c> 一旦直接 new 出 <c>ContentDialog</c>，整个 VM 就再也无法在
/// 没有 XAML 宿主的环境里被驱动，而 §7.3 验收 ②③ 恰恰要求能断言"勾选后只调了 BuildPlan"。</item>
/// <item>T14 的 <c>DialogService</c> 只需实现本接口，两个任务的边界因此是编译期强制的。</item>
/// </list>
/// 这是<b>新增</b>而非改名：方法名、参数顺序、返回值都与 §7.5 逐字一致。
/// </remarks>
public interface IDialogService
{
    /// <summary>一般信息（单按钮）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    Task InfoAsync(string title, string message);

    /// <summary>成功（单按钮）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    Task SuccessAsync(string title, string message);

    /// <summary>警告（单按钮）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    Task WarningAsync(string title, string message);

    /// <summary>错误（单按钮）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    Task ErrorAsync(string title, string message);

    /// <summary>确认开始转移（是/否）。</summary>
    /// <param name="count">待处理项数。</param>
    /// <param name="scaleSummary">规模摘要（未扫描时给 <see cref="UiText.ScaleUnavailable"/>）。</param>
    /// <param name="targetRoot">目标根目录。</param>
    Task<bool> ConfirmTransferAsync(int count, string? scaleSummary, string targetRoot);

    /// <summary>确认恢复系统默认值（是/否）。</summary>
    /// <param name="displays">待恢复项的显示名。</param>
    Task<bool> ConfirmRestoreAsync(IReadOnlyList<string> displays);

    /// <summary>询问是否重启资源管理器（是/否）。</summary>
    Task<bool> AskRestartShellAsync();

    /// <summary>无需转移（单按钮）。</summary>
    Task InfoNoMoveAsync();

    /// <summary>结果弹窗（操作完成 / 部分项目未完成 / 已停止 / 执行出错）。</summary>
    /// <param name="report">执行报告。</param>
    Task ResultAsync(TransferReport report);

    /// <summary>忙碌时退出确认（是/否）。</summary>
    /// <param name="cancelable">当前工作是否可中断（可中断才提示"会停止当前项"）。</param>
    Task<bool> ConfirmExitWhileBusyAsync(bool cancelable);

    /// <summary>上次事务未正常收尾（是/否/取消）。</summary>
    /// <param name="items">未完成项。</param>
    Task<PendingChoice> AskPendingIntentAsync(IReadOnlyList<PendingIntent> items);

    /// <summary>上次事务记录不可用（单按钮）。</summary>
    /// <param name="reason">不可用原因。</param>
    Task WarningPendingUnreadableAsync(string reason);

    /// <summary>提权提示（单按钮）。</summary>
    /// <param name="message">正文。</param>
    Task InfoElevationAsync(string message);

    /// <summary>重复启动（单按钮）。</summary>
    Task WarningAlreadyRunningAsync();

    /// <summary>回滚结果（成功走 Success，失败走 Error；正文含四项计数）。</summary>
    /// <param name="message">正文。</param>
    /// <param name="ok">是否成功。</param>
    Task ShowRollbackResultAsync(string message, bool ok);

    /// <summary>只读文本弹窗（<c>--scan</c> / <c>--version</c> / <c>--help</c> 共用）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="body">正文（等宽、可选中、可滚动）。</param>
    /// <param name="showOpenLogButton">是否在底部露出「打开日志目录」。</param>
    Task ShowTextAsync(string title, string body, bool showOpenLogButton);
}

/// <summary>主界面所处的阶段（决定主按钮文案与可用性矩阵）。</summary>
public enum VmPhase
{
    /// <summary>空闲。</summary>
    Idle,

    /// <summary>正在转移。</summary>
    Running,

    /// <summary>正在停止。</summary>
    Stopping,

    /// <summary>正在恢复系统默认值。</summary>
    Restoring,

    /// <summary>正在重启资源管理器。</summary>
    Restarting,

    /// <summary>正在关窗收尾。</summary>
    Closing,
}

/// <summary>
/// 主界面数据层（架构 §2.3 A08）。
/// </summary>
/// <remarks>
/// 本文件的红线（架构 §7.3 验收 ①）：<b>不含任何业务判定</b>。
/// 具体说：不拼路径（路径合成在 <see cref="RunConfig.ComposeTarget"/>）、
/// 不判注册表（解析在 <c>TransferEngine.ResolveCurrent</c>）、不判空间与禁区
/// （判定在 <see cref="PlanItem.BlockingReason"/>）、不写界面文案（文案全部来自 <see cref="UiText"/>）。
/// 这里只做四件事：把 <see cref="PlanItem"/> 投影成行、分发命令、消费进度、串起弹窗。
/// <para>
/// 另一处红线：任何 <c>TwoWay</c> 写回都只调 <see cref="RecomputePlan"/>，
/// 而它走 <c>scan: false</c>，因此<b>零文件系统枚举</b>。勾一下勾选框去扫 7 个目录的规模，
/// 会让界面在机械盘上卡到无法使用。
/// </para>
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private readonly TransferEngine _engine;
    private readonly IAppPaths _paths;
    private readonly IAppLog _log;
    private readonly IDialogService _dialogs;
    private readonly RunConfig _config;
    private readonly IReadOnlyList<DriveVolume> _drives;
    private readonly ObservableCollection<FolderRowViewModel> _rows = new();
    private readonly Dictionary<string, FolderRowViewModel> _rowByKey = new(StringComparer.Ordinal);
    private readonly RelayCommand _primaryCommand;
    private readonly AsyncRelayCommand _restoreCommand;
    private readonly RelayCommand _openLogCommand;
    private readonly RelayCommand _relaunchCommand;
    private readonly RelayCommand _exitCommand;
    private readonly RelayCommand _selectAllCommand;
    private readonly RelayCommand<FolderRowViewModel> _toggleRowCommand;

    private IReadOnlyList<PlanItem> _plan = Array.Empty<PlanItem>();
    private DriveVolume? _selectedDrive;
    private VmPhase _phase = VmPhase.Idle;
    private CancellationTokenSource? _cts;
    private string _statusText = UiText.Ready;
    private double _progressValue;
    private bool _allSelected = true;
    private bool _suppressSync;
    private bool _workRunning;
    private bool _startPending;
    private bool _interactionEnabled = true;
    private bool _exitRequested;
    private bool _isLogLinkVisible;
    private bool _isElevated;

    /// <summary>构造。</summary>
    /// <param name="engine">引擎（唯一允许触碰注册表与文件系统的出口）。</param>
    /// <param name="paths">落点（日志目录来源）。</param>
    /// <param name="log">日志。</param>
    /// <param name="dialogs">弹窗出口。</param>
    /// <param name="drives">已枚举到的非系统固定盘（由 <c>DriveInventory</c> 在启动时给出）。</param>
    public MainViewModel(
        TransferEngine engine,
        IAppPaths paths,
        IAppLog log,
        IDialogService dialogs,
        IReadOnlyList<DriveVolume> drives)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _drives = drives ?? Array.Empty<DriveVolume>();

        // 重启 explorer 的每一步（PID 快照、taskkill 退出码、拉起通道、存活确认）都必须落日志：
        // 这是全流程唯一"主动杀用户桌面"的环节，报障时没有这些就只剩一句"无法自动拉起"。
        ShellIntegration.DiagnosticsSink = message => _log.Info(message);

        _config = RunConfig.CreateDefault(_drives, engine.DryRun);
        Columns = new TableColumnLayout(DispatcherQueue.GetForCurrentThread());

        _primaryCommand = new RelayCommand(OnPrimary, CanPrimary);
        _restoreCommand = new AsyncRelayCommand(OnRestoreRequested, CanRestore, OnCommandError);
        _openLogCommand = new RelayCommand(OpenLog);
        _relaunchCommand = new RelayCommand(RelaunchAsAdmin, () => !IsDryRun);
        _exitCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        _selectAllCommand = new RelayCommand(() => AllSelected = !AllSelected);
        _toggleRowCommand = new RelayCommand<FolderRowViewModel>(ToggleRow);

        BuildRows();
        _selectedDrive = FindDrive(_config.DestinationRoot);
        _isElevated = ShellIntegration.IsElevated();

        RecomputePlan();
        SyncAllSelected(CountSelected());
    }

    /// <summary>窗口请求关闭（由 code-behind 统一走 <c>AppWindow.Closing</c> 收尾路径）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>表格行（固定 7 行，顺序同 <see cref="Catalog.All"/>）。</summary>
    public ObservableCollection<FolderRowViewModel> Rows => _rows;

    /// <summary>共享列宽对象。</summary>
    public TableColumnLayout Columns { get; }

    /// <summary>4 个列标题（XAML 里不得出现中文字面量，故由 VM 提供）。</summary>
    public string ColumnNameHeader => UiText.ColumnName;

    /// <inheritdoc cref="ColumnNameHeader" />
    public string ColumnCurrentHeader => UiText.ColumnCurrent;

    /// <inheritdoc cref="ColumnNameHeader" />
    public string ColumnTargetHeader => UiText.ColumnTarget;

    /// <inheritdoc cref="ColumnNameHeader" />
    public string ColumnStatusHeader => UiText.ColumnStatus;

    /// <summary>目标根目录标签。</summary>
    public string DestinationLabel => UiText.DestinationLabel;

    /// <summary>表格列表的无障碍名称（ListView 的 <c>AutomationProperties.Name</c> 绑定源）。</summary>
    public string FolderListLabel => UiText.FolderListLabel;

    /// <summary>可用的非系统固定盘。</summary>
    public IReadOnlyList<DriveVolume> Drives => _drives;

    /// <summary>当前选中的目标盘（<c>TwoWay</c>）。</summary>
    public DriveVolume? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (!SetProperty(ref _selectedDrive, value))
            {
                return;
            }

            _config.DestinationRoot = value?.TargetRoot ?? string.Empty;
            RaisePropertyChanged(nameof(SpaceLabel));

            // 目标根整体变了 → 文本整体变短也应当允许列收窄，否则列宽会一直卡在最宽的一次。
            Columns.ResetHighWater();
            RecomputePlan();
        }
    }

    /// <summary>目标盘的可用空间标签（无盘时为提示文案）。</summary>
    public string SpaceLabel => _selectedDrive?.SpaceLabel ?? UiText.NoTargetDisk;

    /// <summary>是否在目标根下再套一层用户名目录（<c>TwoWay</c>）。</summary>
    public bool CreateUserDirectory
    {
        get => _config.CreateUserDirectory;
        set
        {
            if (_config.CreateUserDirectory == value)
            {
                return;
            }

            _config.CreateUserDirectory = value;
            RaisePropertyChanged();
            Columns.ResetHighWater();
            RecomputePlan();
        }
    }

    /// <summary>是否全选（<c>TwoWay</c>，二态）。</summary>
    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            if (!SetProperty(ref _allSelected, value))
            {
                return;
            }

            if (_suppressSync)
            {
                return;
            }

            // 硬前提 4：批量改 7 行必须抑制回环，否则每行 setter 都会反向再同步一次 AllSelected。
            _suppressSync = true;
            try
            {
                foreach (FolderRowViewModel row in _rows)
                {
                    // 「路径相同」的行不参与全选：它本来就不可勾（CanSelect=false），
                    // 硬写 IsSelected 会让"已选择数"大于实际待办数。
                    if (row.IsSameAsCurrent)
                    {
                        row.AutoDeselected = value;
                        continue;
                    }

                    row.IsSelected = value;
                    row.AutoDeselected = false;
                }
            }
            finally
            {
                _suppressSync = false;
            }

            RecomputePlan();
        }
    }

    /// <summary>已选摘要（"可转移 x / y 项 · 无需转移 z 项"）。</summary>
    public string SelectionSummary => UiText.SelectionSummaryDetail(
        CountSelected(),
        CountActionableRows(),
        CountNotNeeded());

    /// <summary>空态提示主标题（徽章下方的强文字，所有目录都已在目标盘时显示）。</summary>
    public string EmptyStateTitle => UiText.EmptyStateAllSameTitle;

    /// <summary>空态提示副文案（主标题下方的弱化说明）。</summary>
    public string EmptyStateSubtitle => UiText.EmptyStateAllSameSubtitle;

    /// <summary>是否显示表格空态提示（7 个目录全部"路径相同"）。</summary>
    public bool ShowEmptyState
    {
        get
        {
            int total = _rows.Count;
            return total > 0 && CountNotNeeded() == total;
        }
    }

    /// <summary>主按钮文案（随阶段变化）。</summary>
    public string PrimaryButtonText => _phase switch
    {
        VmPhase.Running => UiText.ButtonStop,
        VmPhase.Stopping => UiText.ButtonStopping,
        VmPhase.Restoring => UiText.ButtonRestoring,
        VmPhase.Restarting => UiText.ButtonRestarting,
        VmPhase.Closing => UiText.ButtonStopping,
        _ => UiText.ButtonStart,
    };

    /// <summary>主按钮是否可用。</summary>
    public bool IsPrimaryEnabled => _primaryCommand.CanExecute(null);

    /// <summary>是否处于"不可编辑输入"的状态（执行中 / 收尾中 / 外部尚未就绪）。</summary>
    public bool IsInputEnabled => _phase == VmPhase.Idle && _interactionEnabled;

    /// <summary>
    /// 外部就绪开关：宿主在"还不能让用户动手"的阶段置 <c>false</c>（当前唯一用途是
    /// 单实例判定之前的那段窗口期）。
    /// </summary>
    /// <remarks>
    /// 为什么放在视图模型而不是给窗口设 <c>IsEnabled</c>：WinUI 的窗口内容是根 Grid
    /// （<c>Panel</c> → <c>FrameworkElement</c>），没有 <c>IsEnabled</c>；而 <c>IsHitTestVisible</c>
    /// 只挡鼠标、挡不住键盘（Tab 到主按钮再按空格照样触发）。开关落在这里，一次就能同时
    /// 约束主按钮、恢复按钮、全选框和每行的勾选框 —— 那些全都读 <see cref="IsInputEnabled"/>。
    /// </remarks>
    public bool InteractionEnabled
    {
        get => _interactionEnabled;
        set
        {
            if (_interactionEnabled == value)
            {
                return;
            }

            _interactionEnabled = value;
            RaiseProperties(
                nameof(InteractionEnabled),
                nameof(IsInputEnabled),
                nameof(IsPrimaryEnabled),
                nameof(CanToggleSelectAll));
            _primaryCommand.RaiseCanExecuteChanged();
            _restoreCommand.RaiseCanExecuteChanged();
            SyncRowEditability();
        }
    }

    /// <summary>
    /// 全选框是否可勾：输入空闲 <b>且</b> 存在可转移行。
    /// </summary>
    /// <remarks>
    /// 只绑 <see cref="IsInputEnabled"/> 会在"7 行全部路径相同"时留下一个能点但无效的
    /// 全选框（点击后 0 行被选，勾选框视觉却变化），与行勾选框的禁用语义不一致；
    /// 这里与行勾选框对齐：没有可勾的行，全选框同样进入禁用态（2026-11-14 用户反馈）。
    /// </remarks>
    public bool CanToggleSelectAll => IsInputEnabled && CountActionableRows() > 0;

    /// <summary>是否忙碌（引擎任务在跑或正在重启资源管理器）。</summary>
    public bool IsBusy => _workRunning || _phase == VmPhase.Restarting;

    /// <summary>状态栏文本。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>进度条数值（0–100，按当前项的文件完成比例）。</summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value < 0d ? 0d : (value > 100d ? 100d : value));
    }

    /// <summary>是否试运行模式（状态栏徽标）。</summary>
    public bool IsDryRun => _engine.DryRun;

    /// <summary>试运行徽标文案。</summary>
    public string DryRunBadge => UiText.DryRunBadge;

    /// <summary>当前是否已提权。</summary>
    public bool IsElevated => _isElevated;

    /// <summary>提权提示条文案。</summary>
    public string ElevationBody => UiText.ElevationBody;

    /// <summary>
    /// 提权重启前的单实例释放钩子（由 App 注入）。
    /// </summary>
    /// <remarks>
    /// 架构 §8-R7：提权重启前必须先 <c>Release()</c> 并删除 PID 文件——否则新提权实例
    /// 会因「PID 文件里记着一个活着的 DirMover」被误判为重复启动，提权重启永远失败。
    /// 单实例权由 App 持有（<c>SingleInstanceGuard</c> 在 <c>App</c> 里创建），故以钩子注入。
    /// </remarks>
    internal Action? ReleaseSingleInstanceGate { get; set; }

    /// <summary>提权重启失败后恢复单实例权的钩子（由 App 注入；见 <see cref="ReleaseSingleInstanceGate"/>）。</summary>
    internal Action? ReacquireSingleInstanceGate { get; set; }

    /// <summary>提权链接文案。</summary>
    public string RelaunchAsAdminText => UiText.ButtonRelaunchAsAdmin;

    /// <summary>是否露出「打开日志目录」链接（失败时才露出，PRD §4.1）。</summary>
    public bool IsLogLinkVisible
    {
        get => _isLogLinkVisible;
        private set => SetProperty(ref _isLogLinkVisible, value);
    }

    /// <summary>「打开日志目录」链接文案。</summary>
    public string OpenLogText => UiText.ButtonOpenLog;

    /// <summary>主按钮命令（<c>Ctrl+Enter</c> 也走它）。</summary>
    public System.Windows.Input.ICommand PrimaryCommand => _primaryCommand;

    /// <summary>恢复系统默认值命令。</summary>
    public System.Windows.Input.ICommand RestoreDefaultsCommand => _restoreCommand;

    /// <summary>退出程序命令（与窗口 X 同一条收尾路径）。</summary>
    public System.Windows.Input.ICommand ExitCommand => _exitCommand;

    /// <summary>全选/全不选命令。</summary>
    public System.Windows.Input.ICommand SelectAllCommand => _selectAllCommand;

    /// <summary>行级切换命令（空格 / 回车）。</summary>
    public System.Windows.Input.ICommand ToggleRowCommand => _toggleRowCommand;

    /// <summary>打开日志目录命令。</summary>
    public System.Windows.Input.ICommand OpenLogCommand => _openLogCommand;

    /// <summary>以管理员身份重启命令（试运行时不提供）。</summary>
    public System.Windows.Input.ICommand RelaunchAsAdminCommand => _relaunchCommand;

    /// <summary>恢复按钮文案。</summary>
    public string RestoreDefaultsText => UiText.ButtonRestoreDefaults;

    /// <summary>退出按钮文案。</summary>
    public string ExitText => UiText.ButtonExit;

    /// <summary>全选勾选框文案。</summary>
    public string SelectAllText => UiText.CheckBoxSelectAll;

    /// <summary>创建用户目录勾选框文案。</summary>
    public string CreateUserDirectoryText => UiText.CheckBoxCreateUserDirectory;

    /// <summary>
    /// 切换一行勾选并重算计划（行首勾选框之外的入口：空格 / 回车）。
    /// </summary>
    /// <param name="row">目标行。</param>
    public void ToggleRow(FolderRowViewModel row)
    {
        if (row is null || !IsInputEnabled)
        {
            return;
        }

        row.IsSelected = !row.IsSelected;
        RecomputePlan();
    }

    /// <summary>
    /// 只重新计算"目标 / 状态"，<b>零读盘</b>（<c>scan: false</c>）。
    /// </summary>
    /// <remarks>
    /// 7 行<b>全部</b>重算而不是只算勾选项：未勾选行的「转移目标」列同样要显示"如果勾上会搬到哪"，
    /// 只算勾选项会让未勾选行的目标列停在上一轮的旧值。
    /// <para>
    /// 计划实例被缓存进 <c>_plan</c> 供执行时直接复用：执行前再 <c>BuildPlan</c> 一次会重复读 14 次注册表，
    /// 更糟的是中间若被外部改动，"确认框里说的"和"实际执行的"会不是同一份计划。
    /// </para>
    /// <para>
    /// 单次成本（审计 A-13 实测，2026-11-13，Release、本机 7 项、真实 HKCU 读取）：
    /// 这里做的全部工作是 7 ×（读注册表 1–3 个值 + <c>RegistryTargets</c> 的 Exists 探测）+
    /// 每项一次 <c>ReparseAncestor</c>（沿祖先链 <c>File.GetAttributes</c>）+ 少量纯字符串比较，
    /// <c>TransferEngine.BuildPlan(scan:false)</c> 中位数 <b>0.8 ms</b>（n=30，max 0.93 ms）。
    /// 没有阻塞式 I/O 放大：注册表读失败不重试、<c>ReparseAncestor</c> 每项只算一次，
    /// 且路径来自本机非系统固定盘（不含网络路径）。
    /// 因此<b>保持同步、保持全量重算</b>：改异步会让勾选后的状态列慢一拍，改防抖会让
    /// "勾上 → 立刻看到目标与摘要变化"这条即时反馈断掉，两者都不值得为 0.8 ms 付出。
    /// </para>
    /// </remarks>
    public void RecomputePlan()
    {
        _plan = _engine.BuildPlan(
            Catalog.AllKeys,
            _config.DestinationRoot,
            _config.CreateUserDirectory,
            Environment.UserName,
            scan: false);

        foreach (PlanItem item in _plan)
        {
            if (_rowByKey.TryGetValue(item.Key, out FolderRowViewModel? row))
            {
                row.Apply(item);
            }
        }

        // 「路径相同」的结论直接决定这一行能不能勾（见 FolderRowViewModel.CanSelect），
        // 所以必须在计划刷新后立刻同步勾选状态，否则摘要与主按钮会比界面慢一拍。
        SyncRowSelection();
        SyncAllSelected(CountSelected());

        RaisePropertyChanged(nameof(SelectionSummary));
        RaisePropertyChanged(nameof(ShowEmptyState));
        // 切盘会改变可转移行数（0 ↔ 7），全选框的禁用态跟着变。
        RaisePropertyChanged(nameof(CanToggleSelectAll));
        RaisePropertyChanged(nameof(PrimaryButtonText));
        RaisePropertyChanged(nameof(IsPrimaryEnabled));
        _primaryCommand.RaiseCanExecuteChanged();
        // 「当前位置」列刷新会改变"已离开默认位置"的行数，恢复按钮的可用性必须同步。
        _restoreCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 关窗收尾（架构 §7.6）。返回 <c>true</c> 表示可以真正关闭窗口。
    /// </summary>
    public async Task<bool> RequestCloseAsync()
    {
        if (!IsBusy)
        {
            return true;
        }

        // Restarting 阶段不是"可中断的引擎任务"：它只是等 explorer 回来，不碰用户数据，
        // 而 _cts 此时为 null（没有可取消的对象），旧实现会白等满 ExitFinishTimeout 才放行，
        // 用户看到的是"关窗口要卡一分钟"。这里直接放行：重启流程由 ShellIntegration
        // 自己的超时兜底，窗口关掉不影响它把 explorer 拉起来。
        if (!_workRunning)
        {
            _exitRequested = true;
            _log.Info($"收尾时处于阶段 {_phase}（无引擎任务在跑），不等待，直接关闭");
            return true;
        }

        try
        {
            if (!await _dialogs.ConfirmExitWhileBusyAsync(_workRunning))
            {
                return false;
            }

            _exitRequested = true;

            // 可中断阶段：先置取消位，让引擎在下一个检查点收尾（它自己会保证"不删源、不写注册表"）。
            _cts?.Cancel();

            SetPhase(VmPhase.Closing);
            StatusText = UiText.SafeShutdown;

            DateTime deadline = DateTime.UtcNow + Catalog.ExitFinishTimeout;
            while (IsBusy && DateTime.UtcNow < deadline)
            {
                await Task.Delay(Catalog.ExitFinishPollInterval);
            }

            if (IsBusy)
            {
                // 超时后强制关闭是刻意选择：卡住的项已经无法保证进度，继续挂着只会让用户以为程序死了。
                _log.Error($"关窗收尾超时（阶段 {_phase}），强制关闭", null);
            }

            return true;
        }
        catch (Exception ex)
        {
            // 收尾本身抛异常（弹窗服务失败、Delay 被中断等）不能让窗口关不掉。
            // 放行是最安全的：引擎的取消位已置，它会在下一个检查点自行收尾，不删源也不写注册表。
            _log.Error("关窗收尾失败，改为放行关闭", ex);
            _exitRequested = true;
            return true;
        }
    }

    private void BuildRows()
    {
        _rows.Clear();
        _rowByKey.Clear();

        IReadOnlyList<FolderSpec> specs = Catalog.All;
        for (int i = 0; i < specs.Count; i++)
        {
            // 0 基索引为奇 → 界面上的偶数行 → 走斑马纹 Alt（架构 §7.4）。
            var row = new FolderRowViewModel(specs[i], isOdd: (i % 2) == 1, Columns)
            {
                IsSelected = true,
            };

            _rows.Add(row);
            _rowByKey[specs[i].Key] = row;

            // 行内勾选框是 TwoWay 绑定，点一下只会改行对象本身；不订阅的话摘要、全选框、
            // 主按钮的可用性都停在旧值上（用户会看到"勾了但按钮还是灰的"）。
            row.PropertyChanged += OnRowPropertyChanged;
        }
    }

    /// <summary>行勾选变化的回传：摘要 / 全选框 / 主按钮可用性都依赖它。</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSync || e.PropertyName != nameof(FolderRowViewModel.IsSelected))
        {
            return;
        }

        SyncAllSelected(CountSelected());
        RaisePropertyChanged(nameof(SelectionSummary));
        RaisePropertyChanged(nameof(IsPrimaryEnabled));
        _primaryCommand.RaiseCanExecuteChanged();
    }

    private DriveVolume? FindDrive(string targetRoot)
    {
        foreach (DriveVolume drive in _drives)
        {
            if (string.Equals(drive.TargetRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                return drive;
            }
        }

        return _drives.Count > 0 ? _drives[0] : null;
    }

    private int CountSelected()
    {
        int count = 0;
        foreach (FolderRowViewModel row in _rows)
        {
            if (row.IsSelected)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>可转移行数（当前位置与目标位置不同的行）。</summary>
    private int CountActionableRows()
    {
        int count = 0;
        foreach (FolderRowViewModel row in _rows)
        {
            if (!row.IsSameAsCurrent)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>无需转移行数（当前位置＝目标位置）。</summary>
    private int CountNotNeeded() => _rows.Count - CountActionableRows();

    /// <summary>
    /// 把新计划里的"路径相同"结论落到勾选状态上。
    /// </summary>
    /// <remarks>
    /// 两个方向都要处理：
    /// ① 变得"路径相同"的行 —— 取消勾选并打标记（不可勾的行被勾着，摘要就会虚高）；
    /// ② 重新变得可转移的行且当初是被规则取消的 —— 勾回来，用户不必手动再勾一遍。
    /// </remarks>
    private void SyncRowSelection()
    {
        _suppressSync = true;
        try
        {
            foreach (FolderRowViewModel row in _rows)
            {
                if (row.IsSameAsCurrent)
                {
                    if (row.IsSelected)
                    {
                        row.IsSelected = false;
                        row.AutoDeselected = true;
                    }

                    continue;
                }

                if (row.AutoDeselected && !row.IsSelected)
                {
                    row.IsSelected = true;
                    row.AutoDeselected = false;
                }
            }
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private void SyncAllSelected(int selected)
    {
        // 分母是可转移项数而不是总行数：「路径相同」的行不可勾，用总行数会让全选框永远打不上。
        int actionable = CountActionableRows();
        bool all = actionable > 0 && selected >= actionable;
        if (_allSelected == all)
        {
            return;
        }

        _suppressSync = true;
        try
        {
            SetProperty(ref _allSelected, all, nameof(AllSelected));
        }
        finally
        {
            _suppressSync = false;
        }
    }

    /// <summary>
    /// 组出待执行清单：勾选 <b>且</b> 尚未被真正落实过的项。
    /// </summary>
    /// <remarks>
    /// 为什么要按 <see cref="FolderRowViewModel.IsActionable"/> 过滤：对已完成的项再执行一次，
    /// 引擎会命中"当前位置不存在"并判定为失败——一次成功的结果会被一堆红色失败项染红，
    /// 而用户其实只是又点了一下主按钮。
    /// <para>
    /// 刻意<b>不</b>自动取消已完成项的勾选：取消勾选会让行状态落到 <c>Disabled</c>，
    /// 那片绿色/红色结果着色会立刻消失，与 PRD §4.1-10「保留到下次刷新」相反。
    /// </para>
    /// </remarks>
    private List<PlanItem> SelectedPlan()
    {
        var selected = new List<PlanItem>(_rows.Count);
        foreach (PlanItem item in _plan)
        {
            if (_rowByKey.TryGetValue(item.Key, out FolderRowViewModel? row)
                && row.IsSelected
                && row.IsActionable)
            {
                selected.Add(item);
            }
        }

        return selected;
    }

    /// <summary>勾选且仍可执行的项数（决定主按钮/恢复按钮是否可用）。</summary>
    private int CountActionable()
    {
        int count = 0;
        foreach (FolderRowViewModel row in _rows)
        {
            if (row.IsSelected && row.IsActionable)
            {
                count++;
            }
        }

        return count;
    }

    private bool CanPrimary()
    {
        if (!_interactionEnabled)
        {
            // 外部尚未就绪（单实例判定之前）：主按钮一律不可用，连"点两次变停止"也不给。
            return false;
        }

        return _phase switch
        {
            VmPhase.Running => true,
            VmPhase.Idle => _drives.Count > 0 && CountActionable() > 0,
            _ => false,
        };
    }

    private bool CanRestore() => _interactionEnabled && _phase == VmPhase.Idle && CountRestorable() > 0;

    /// <summary>
    /// 某一行是否值得恢复：当前位置与系统出厂默认位置不是同一路径。
    /// </summary>
    /// <remarks>
    /// 恢复语义与"转移勾选"无关——勾选框表达的是"要搬到哪"，而恢复只看"现在是否不在默认位置"。
    /// 转移完成后行会变成"路径相同"并被自动取消勾选（勾选框随之禁用），
    /// 若恢复仍依赖勾选状态，按钮会在用户最需要它的时刻永远灰着。
    /// 路径比较（含环境变量展开、大小写与尾分隔符）属于 Core，这里只调用不实现。
    /// </remarks>
    private static bool IsRestorable(FolderRowViewModel row) =>
        !PathTools.ResolvesToSame(row.Current, PathTools.Expand(row.Spec.DefaultPath));

    /// <summary>已离开系统默认位置的行数（决定恢复按钮是否可用）。</summary>
    private int CountRestorable()
    {
        int count = 0;
        foreach (FolderRowViewModel row in _rows)
        {
            if (IsRestorable(row))
            {
                count++;
            }
        }

        return count;
    }

    private void OnPrimary()
    {
        // 键盘快捷键（Ctrl+Enter）不经过 CanExecute，只靠按钮"变灰"挡不住它，
        // 所以这道判定必须在命令体里再做一次。
        if (!_interactionEnabled)
        {
            return;
        }

        if (_phase == VmPhase.Running)
        {
            Stop();
            return;
        }

        if (_phase != VmPhase.Idle)
        {
            return;
        }

        // 同步重入闸门（与 _phase 分开）。
        //
        // _phase 要到 ExecuteCoreAsync 里才变成 Running，而 StartTransferAsync 在此之前
        // 还有 await 确认框等异步步骤。这段窗口期内 _phase 仍是 Idle，于是：
        //   · 鼠标双击、或 Ctrl+Enter 的 KeyBinding（不经过 CanExecute）能二次进入；
        //   · 两次都会走完确认框，然后各自建自己的 CTS 跑同一份计划 ——
        //     同一批目录被两个引擎实例并发搬运，是会真的损坏数据的。
        // 置位必须发生在任何 await 之前的同一线程上，所以放在这里而不是 StartTransferAsync 内。
        if (_startPending)
        {
            return;
        }

        _startPending = true;

        // 不 `_ = StartTransferAsync()` 直接丢弃：主按钮是同步命令，
        // 一旦异步体抛异常就没有任何 catch，WinUI 会把它变成"点一下程序消失"。
        _ = SafeStartAsync();
    }

    private async Task SafeStartAsync()
    {
        try
        {
            await StartTransferAsync();
        }
        catch (Exception ex)
        {
            OnCommandError(ex);
        }
        finally
        {
            // 无论从哪条路径退出都要开闸，否则一次异常会让主按钮永久失效（点不动也没提示）。
            _startPending = false;
        }
    }

    private void OnCommandError(Exception ex)
    {
        _log.Error("界面命令执行失败", ex);
        StatusText = UiText.UiRefreshFailed;
        IsLogLinkVisible = true;
    }

    private async Task StartTransferAsync()
    {
        List<PlanItem> selected = SelectedPlan();
        if (selected.Count == 0)
        {
            await WarnNothingToDoAsync();
            return;
        }

        bool anyMove = false;
        string firstBlock = string.Empty;
        int blockedCount = 0;
        foreach (PlanItem item in selected)
        {
            string reason = item.BlockingReason();
            if (reason.Length > 0)
            {
                blockedCount++;
                if (firstBlock.Length == 0)
                {
                    // 直接用 BlockingReason() 的整句：它是 UiText 给出的完整中文句子（已含路径）。
                    // 在这里拼"显示名 + 分隔符 + 原因"就等于在 VM 里造文案，架构 §7.3 明令禁止。
                    firstBlock = reason;
                }
            }

            anyMove |= item.NeedsMove;
        }

        if (!anyMove)
        {
            // 全被阻断时先说清第一项的原因：让用户点下去才失败，等于把"不可执行"藏起来。
            if (blockedCount > 0)
            {
                await _dialogs.ErrorAsync(UiText.DialogCannotStart, firstBlock);
                IsLogLinkVisible = true;
                return;
            }

            await _dialogs.InfoNoMoveAsync();
            return;
        }

        // 计划是 scan: false 建的，这里如实说"规模在执行时统计"，绝不为了填一个数字去扫盘。
        if (!await _dialogs.ConfirmTransferAsync(selected.Count, UiText.ScaleUnavailable, _config.DestinationRoot))
        {
            return;
        }

        foreach (PlanItem item in selected)
        {
            if (_rowByKey.TryGetValue(item.Key, out FolderRowViewModel? row))
            {
                row.MarkPending();
            }
        }

        await ExecuteCoreAsync(
            restoring: false,
            (progress, observer, token) => _engine.ExecuteAsync(selected, progress, observer, token));
    }

    /// <summary>
    /// 待执行清单为空时的提示，区分"什么都没勾"与"勾了但都已落实过"。
    /// </summary>
    /// <remarks>
    /// 混成一句"请先勾选"会让刚跑完一轮的用户以为自己勾选丢了——勾选其实还在，
    /// 只是那些项已经没有可做的事。这是"界面说错了下一步动作"的典型情形。
    /// </remarks>
    private async Task WarnNothingToDoAsync()
    {
        if (CountSelected() > 0)
        {
            await _dialogs.InfoNoMoveAsync();
            return;
        }

        await _dialogs.WarningAsync(UiText.DialogNoSelection, UiText.NoSelectionWarning);
    }

    private async Task OnRestoreRequested()
    {
        if (!CanRestore())
        {
            return;
        }

        // 恢复清单按"已离开默认位置"取，不按勾选取（理由见 IsRestorable 的 remarks）。
        List<string> keys = new(_rows.Count);
        List<string> displays = new(_rows.Count);
        foreach (FolderRowViewModel row in _rows)
        {
            if (IsRestorable(row))
            {
                keys.Add(row.Key);
                displays.Add(row.Display);
            }
        }

        if (keys.Count == 0)
        {
            await WarnNothingToDoAsync();
            return;
        }

        if (!await _dialogs.ConfirmRestoreAsync(displays))
        {
            return;
        }

        await ExecuteCoreAsync(
            restoring: true,
            (progress, observer, token) => _engine.RestoreDefaultsAsync(keys, progress, observer, token));
    }

    private async Task ExecuteCoreAsync(
        bool restoring,
        Func<ITransferProgress, IItemObserver, CancellationToken, Task<TransferReport>> run)
    {
        _workRunning = true;
        SetPhase(restoring ? VmPhase.Restoring : VmPhase.Running);
        RaisePropertyChanged(nameof(IsBusy));

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        // 在 UI 线程构造 Progress<T>，回调天然回到 UI 线程；Core 因此不需要认识 DispatcherQueue。
        ITransferProgress progress =
            new ThrottledTransferProgress(new TransferProgressBridge(new Progress<TransferProgressInfo>(OnProgress)));
        IItemObserver observer = new UiItemObserver(DispatcherQueue.GetForCurrentThread(), OnItemStarted, OnItemFinished);

        TransferReport? report = null;
        try
        {
            report = await run(progress, observer, _cts.Token);

            ApplyOutcomes(report);

            // 必须重取计划：源已经搬走，缓存里那份的 Current 已失效。
            // 不重取的话主按钮重新可用，用户再点一次就会去搬一个不存在的源。
            RefreshPlanPreservingOutcomes();

            if (!_exitRequested)
            {
                StatusText = UiText.Ready;
            }
        }
        catch (Exception ex)
        {
            _log.Error("执行未预期失败", ex);
            IsLogLinkVisible = true;

            if (!_exitRequested)
            {
                await _dialogs.ErrorAsync(UiText.ResultError, ex.Message);
            }
        }
        finally
        {
            // 复位只此一处。此前成功路径与 catch 各写一份，功能虽对但结构是隐患：
            // 只要有人在 ApplyOutcomes 与 RefreshPlanPreservingOutcomes 之间插一句提前 return，
            // _phase 就会永远停在 Running —— 主按钮显示"停止"，而点了只是取消一个已结束的任务。
            // finally 里不许 await：会和 _exitRequested 的"收尾期间不弹窗"规则打架。
            _workRunning = false;
            _cts?.Dispose();
            _cts = null;
            SetPhase(VmPhase.Idle);
            RaisePropertyChanged(nameof(IsBusy));
            ProgressValue = 0;
        }

        if (report is null)
        {
            return;
        }

        // 收尾期间不弹结果窗、不弹错误窗、不执行"重启资源管理器"（架构 §7.6 第 6 条）。
        if (_exitRequested)
        {
            return;
        }

        await _dialogs.ResultAsync(report);

        if (report.Cancelled || !report.Ok)
        {
            IsLogLinkVisible = true;
            return;
        }

        await OfferRestartShellAsync();
    }

    /// <summary>
    /// 执行结束后重新取计划，但保留行上的完成/失败着色。
    /// </summary>
    /// <remarks>
    /// 两个都不能省：
    /// <list type="number">
    /// <item>不重取计划 → 主按钮重新可用后，第二次点击会拿旧 <c>Current</c> 去搬已搬走的源。</item>
    /// <item>重取时顺手清着色 → 「完成/失败」颜色在用户眼皮底下瞬间消失（PRD §4.1-10 要求保留到下次刷新）。</item>
    /// </list>
    /// 仍然走 <c>scan: false</c>：这里只需要当前位置与目标，不需要规模。
    /// </remarks>
    private void RefreshPlanPreservingOutcomes()
    {
        _plan = _engine.BuildPlan(
            Catalog.AllKeys,
            _config.DestinationRoot,
            _config.CreateUserDirectory,
            Environment.UserName,
            scan: false);

        foreach (PlanItem item in _plan)
        {
            if (_rowByKey.TryGetValue(item.Key, out FolderRowViewModel? row))
            {
                row.Apply(item, resetOutcome: false);
            }
        }

        // 与 RecomputePlan 对齐：转移完成后这些行的"当前位置 == 目标"已成立，
        // CanSelect 变成 false，而 IsSelected 还停在 true —— 摘要会显示"已选择 N 项"、
        // 全选框也还亮着，可实际上没有一项可执行。用户点一下全选框，那些已完成的
        // 绿色行会被切成 Disabled（绿色消失），与"结果保留到下次刷新"的约定相悖。
        SyncRowSelection();
        SyncAllSelected(CountSelected());

        RaisePropertyChanged(nameof(SelectionSummary));
        RaisePropertyChanged(nameof(ShowEmptyState));
        RaisePropertyChanged(nameof(CanToggleSelectAll));
        RaisePropertyChanged(nameof(PrimaryButtonText));
        RaisePropertyChanged(nameof(IsPrimaryEnabled));
        _primaryCommand.RaiseCanExecuteChanged();

        // 执行结束后"当前位置"变了，恢复按钮的可用性跟着变（同 RecomputePlan 的处理）。
        _restoreCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 启动期检查遗留事务（架构 §4.4 时序图 ③、§8-R5、T14-⑥）：三选弹窗 → 回滚 / 忽略 / 保留。
    /// </summary>
    /// <remarks>
    /// 由 App 在窗口 <c>Loaded</c> 之后再延迟 <see cref="Catalog.PendingIntentDelay"/> 调用
    /// （不是构造函数：弹窗需要 <c>XamlRoot</c>，且不能挡住首帧）。三种结果：
    /// ① 记录干净 / 读错误 → 提示后返回；② 忽略 → 整份注销；③ 回滚 → 只还原未完成项涉及的注册表值。
    /// 回滚只动注册表、不搬文件（文案与 PRD §4.3 一致）；文件搬回走「恢复系统默认值」。
    /// </remarks>
    public async Task CheckPendingIntentsAsync()
    {
        string backupDir = _paths.BackupDirectory;
        PendingIntents pending = IntentJournal.ReadPending(backupDir);
        if (pending.Clean)
        {
            return;
        }

        if (pending.ReadError is not null)
        {
            // 半截 JSON / 结构异常：无法判断上次是否半完成，也无法据此裁剪回滚范围 → 只能警示。
            _log.Error($"意图日志不可读：{pending.ReadError}", null);
            await _dialogs.WarningPendingUnreadableAsync(UiText.PendingUnreadableAction);
            return;
        }

        if (pending.Items.Count == 0)
        {
            return;
        }

        PendingChoice choice = await _dialogs.AskPendingIntentAsync(pending.Items);
        switch (choice)
        {
            case PendingChoice.Rollback:
                await RollbackPendingAsync(backupDir, pending);
                return;
            case PendingChoice.Ignore:
                // 「否」：忽略并删除记录（用户已确认按现状继续使用）。
                IntentJournal.Open(backupDir, enabled: true, _log).Discard();
                return;
        }

        // Keep：保留记录，下次启动再提示。什么都不做。
    }

    /// <summary>按未完成项回滚注册表（只动注册表，不动文件），并展示四项计数结果。</summary>
    private async Task RollbackPendingAsync(string backupDir, PendingIntents pending)
    {
        ISet<RegistryTarget> targets = IntentJournal.UnfinishedRegistryTargets(pending);

        // 旧格式记录（registry 字段缺失）不允许裁剪回滚范围：宁可整份回滚，
        // 也不能凭残缺信息缩小范围（IntentJournal.UnfinishedRegistryTargets 的 remarks）。
        bool mustRollbackFull = false;
        var snapshotPaths = new List<string>();
        foreach (PendingIntent item in pending.Items)
        {
            if (item.Stage == TransferStage.SourceRemoved)
            {
                continue;
            }

            if (!item.RegistryKnown)
            {
                mustRollbackFull = true;
            }

            if (!string.IsNullOrWhiteSpace(item.SnapshotPath) && !snapshotPaths.Contains(item.SnapshotPath))
            {
                snapshotPaths.Add(item.SnapshotPath);
            }
        }

        int restored = 0;
        int removed = 0;
        int skippedCompleted = 0;
        int unknownSkipped = 0;
        bool ok = true;
        string error = string.Empty;

        try
        {
            foreach (string path in snapshotPaths)
            {
                RegistrySnapshot snapshot = RegistryStore.LoadSnapshot(path);
                if (!mustRollbackFull)
                {
                    (RegistrySnapshot trimmed, int excluded) = SnapshotTrimmer.Restrict(snapshot, targets);
                    snapshot = trimmed;
                    skippedCompleted += excluded;
                }

                (int itemRestored, int itemRemoved) = _engine.Store.Rollback(snapshot);
                restored += itemRestored;
                removed += itemRemoved;
                unknownSkipped += _engine.Store.LastRollbackSkipped;
            }

            // 回滚完成后记录使命已尽：整份注销（含已完成项，它们不该在下次启动再提示）。
            IntentJournal.Open(backupDir, enabled: true, _log).Discard();
            RecomputePlan();
        }
        catch (Exception ex)
        {
            // 回滚失败：保留记录（下次启动仍会提示），把原因逐字给用户。
            ok = false;
            error = ex.Message;
            _log.Error("回滚上次未完成事务失败", ex);
        }

        await _dialogs.ShowRollbackResultAsync(
            ok ? UiText.RollbackResult(restored, removed, skippedCompleted, unknownSkipped) : error,
            ok);
    }

    private void ApplyOutcomes(TransferReport report)
    {
        foreach (ItemOutcome outcome in report.Outcomes)
        {
            if (_rowByKey.TryGetValue(outcome.Key, out FolderRowViewModel? row))
            {
                row.ApplyOutcome(outcome);
            }
        }

        if (report.Failed.Count > 0 || report.Notes.Count > 0)
        {
            IsLogLinkVisible = true;
        }
    }

    private void OnItemStarted(int index, int total, PlanItem item)
    {
        if (_rowByKey.TryGetValue(item.Key, out FolderRowViewModel? row))
        {
            row.MarkActive();
        }

        StatusText = UiText.Processing(item.Display, Math.Max(index - 1, 0), Math.Max(total, 0));
    }

    private void OnItemFinished(int index, int total, PlanItem item, ItemOutcome outcome)
    {
        if (_rowByKey.TryGetValue(item.Key, out FolderRowViewModel? row))
        {
            row.ApplyOutcome(outcome);
        }

        StatusText = UiText.Processing(item.Display, Math.Max(index, 0), Math.Max(total, 0));
    }

    private void OnProgress(TransferProgressInfo info)
    {
        // 进度只有"本项"的粒度，整体进度按本项文件完成比例给出；
        // 跨项加权需要事先扫描规模，而界面常态是 scan: false——那会引入读盘。
        ProgressValue = info.TotalInItem > 0 ? 100.0 * info.DoneInItem / info.TotalInItem : 0d;
        StatusText = UiText.Processing(info.Display, Math.Max(info.DoneInItem, 0), Math.Max(info.TotalInItem, 0));
    }

    private void Stop()
    {
        if (_phase != VmPhase.Running && _phase != VmPhase.Restoring)
        {
            return;
        }

        SetPhase(VmPhase.Stopping);
        StatusText = UiText.Stopping;
        _cts?.Cancel();
    }

    private async Task OfferRestartShellAsync()
    {
        if (IsDryRun)
        {
            return;
        }

        if (!ShellIntegration.AutoRestartShellEnabled(_engine.Store))
        {
            // 系统开关为 0：用户/策略已明确表示不让程序动 explorer，那就连问都不问。
            return;
        }

        if (!await _dialogs.AskRestartShellAsync())
        {
            return;
        }

        SetPhase(VmPhase.Restarting);
        StatusText = UiText.RestartingShell;
        try
        {
            ShellOpResult result = await ShellIntegration.RestartExplorerAsync();
            // 分步日志已由 DiagnosticsSink 落盘，这里再记一条最终结论，方便从日志尾部直接看结果。
            _log.Info(result.Ok ? "重启资源管理器：成功" : $"重启资源管理器：失败（{result.Message}）");
            StatusText = result.Ok ? UiText.Ready : result.Message;
            if (!result.Ok)
            {
                IsLogLinkVisible = true;
            }
        }
        catch (Exception ex)
        {
            // 这一步跑在 ExecuteCoreAsync 的 finally 之后（此时 IsBusy 已是 false），
            // 异常若冒出去只会变成一条"界面命令执行失败"，而资源管理器可能已经被杀掉了。
            // 必须显式告知"请手动运行 explorer.exe"，否则用户面对的是一个没有桌面的屏幕。
            _log.Error("重启资源管理器失败", ex);
            StatusText = UiText.ShellRestartManual;
            IsLogLinkVisible = true;
        }
        finally
        {
            SetPhase(VmPhase.Idle);
        }
    }

    private void OpenLog()
    {
        ShellOpResult result = ShellIntegration.OpenDirectory(_paths.LogDirectory);
        StatusText = result.Ok
            ? UiText.OpenLogOk(_paths.LogDirectory)
            : UiText.OpenLogFailed(_paths.LogDirectory);
    }

    private void RelaunchAsAdmin()
    {
        // 架构 §8-R7：先释放单实例权并删除 PID 文件。不释放的话，新提权实例会在
        // SingleInstanceGuard.Acquire 里读到「活着的 DirMover + 被占用的互斥体」，
        // 弹一句「程序已在运行」后退出——提权重启永远失败。
        ReleaseSingleInstanceGate?.Invoke();

        ShellOpResult result = ShellIntegration.RelaunchAsAdmin(string.Empty);
        if (result.Ok)
        {
            // 新实例即将接管：旧实例退出，避免「未提权旧实例 + 已提权新实例」双实例并存
            // （两个实例同时迁移是数据事故，见 SingleInstanceGuard 的 remarks）。
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 失败（含 UAC 被取消）：恢复单实例权，本窗口继续可用。
        ReacquireSingleInstanceGate?.Invoke();
        StatusText = result.Message;
        IsLogLinkVisible = true;
    }

    private void SetPhase(VmPhase phase)
    {
        if (_phase == phase)
        {
            return;
        }

        _phase = phase;
        RaiseProperties(
            nameof(PrimaryButtonText),
            nameof(IsPrimaryEnabled),
            nameof(IsInputEnabled),
            nameof(CanToggleSelectAll),
            nameof(IsBusy));

        _primaryCommand.RaiseCanExecuteChanged();
        _restoreCommand.RaiseCanExecuteChanged();

        SyncRowEditability();
    }

    /// <summary>
    /// 把"可编辑"下发到每一行。
    /// </summary>
    /// <remarks>
    /// 行内勾选框的可操作性要跟着阶段走。行模板只能用 <c>{Binding}</c>（硬前提 2），
    /// 所以"可编辑"必须落到每一行上，而不能在模板里指回列表控件。
    /// 阶段变化与 <see cref="InteractionEnabled"/> 变化都走这里，避免两处各写一份判定。
    /// </remarks>
    private void SyncRowEditability()
    {
        bool editable = _phase == VmPhase.Idle && _interactionEnabled;
        foreach (FolderRowViewModel row in _rows)
        {
            row.IsEditable = editable;
        }
    }

    /// <summary>
    /// 把引擎的项级事件切回 UI 线程。
    /// </summary>
    /// <remarks>
    /// 引擎在后台线程回调（架构 §3.5 明确"在调用线程上触发，实现方自行切回 UI 线程"）。
    /// 若这里不 <c>TryEnqueue</c> 而直接改属性，WinUI 会抛
    /// <c>RPC_E_WRONG_THREAD</c>，表现为"跑到一半界面卡住"。
    /// </remarks>
    private sealed class UiItemObserver : IItemObserver
    {
        private readonly DispatcherQueue? _dispatcher;
        private readonly Action<int, int, PlanItem> _started;
        private readonly Action<int, int, PlanItem, ItemOutcome> _finished;

        internal UiItemObserver(
            DispatcherQueue? dispatcher,
            Action<int, int, PlanItem> started,
            Action<int, int, PlanItem, ItemOutcome> finished)
        {
            _dispatcher = dispatcher;
            _started = started;
            _finished = finished;
        }

        public void OnItemStarted(int index, int total, PlanItem item)
        {
            if (_dispatcher is null)
            {
                return;
            }

            _dispatcher.TryEnqueue(() => _started(index, total, item));
        }

        public void OnItemFinished(int index, int total, PlanItem item, ItemOutcome outcome)
        {
            if (_dispatcher is null)
            {
                return;
            }

            _dispatcher.TryEnqueue(() => _finished(index, total, item, outcome));
        }
    }
}
