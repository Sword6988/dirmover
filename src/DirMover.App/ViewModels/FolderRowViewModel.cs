using DirMover.App.Views;
using DirMover.Core;
using DirMover.Core.Engine;

namespace DirMover.App.ViewModels;

/// <summary>
/// 行着色 7 态（架构 §7.4）。语义只描述"这一行现在是什么状态"，<b>不</b>描述颜色：
/// 具体画刷由 <c>Controls/RowStateConverters</c> 查 <c>Theme/Tokens.xaml</c> 得到。
/// </summary>
/// <remarks>
/// 把颜色和状态分开，是为了让"深色/浅色主题"只改令牌文件，不用碰任何逻辑；
/// 也让这里的 7 个值可以被单测断言而无需构造 <c>Brush</c>。
/// </remarks>
public enum RowState
{
    /// <summary>奇数行、已勾选。</summary>
    Normal,

    /// <summary>偶数行、已勾选（斑马纹）。</summary>
    Alt,

    /// <summary>未勾选。</summary>
    Disabled,

    /// <summary>本次将转移（确认之后）。</summary>
    Pending,

    /// <summary>处理中。</summary>
    Active,

    /// <summary>完成（保留到下次刷新）。</summary>
    Done,

    /// <summary>失败。</summary>
    Failed,
}

/// <summary>
/// 表格一行的绑定对象（架构 §7.2）。
/// </summary>
/// <remarks>
/// 这一层<em>不解</em>读任何东西：状态列的句子由 <see cref="PlanItem.Describe"/> 给出，
/// 目标路径由 Core 合成。行 VM 只做三件事：比较、赋值、通知 —— 任何"路径该怎么拼"的判断
/// 出现在这里都是缺陷（架构 §7.3 硬前提：MainViewModel 与行 VM 都不含业务判定）。
/// </remarks>
public sealed class FolderRowViewModel : ObservableObject
{
    private readonly TableColumnLayout _columns;
    private string _current = string.Empty;
    private string _target = string.Empty;
    private string _status = UiText.StatusPending;
    private bool _isSelected;
    private RowState? _transient;
    private bool _isEditable = true;
    private bool _same;

    /// <summary>构造。</summary>
    /// <param name="spec">静态规格（真源）。</param>
    /// <param name="isOdd">是否奇数索引（0 基）：决定斑马纹走 <see cref="RowState.Alt"/> 还是 <see cref="RowState.Normal"/>。</param>
    /// <param name="columns">共享列宽对象。</param>
    public FolderRowViewModel(FolderSpec spec, bool isOdd, TableColumnLayout columns)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _columns = columns ?? throw new ArgumentNullException(nameof(columns));
        IsOdd = isOdd;
    }

    /// <summary>静态规格。</summary>
    public FolderSpec Spec { get; }

    /// <summary>稳定标识（与 <see cref="FolderSpec.Key"/> 同值）。</summary>
    public string Key => Spec.Key;

    /// <summary>「文件夹名称」列。</summary>
    public string Display => Spec.Display;

    /// <summary>是否奇数索引（0 基）。构造后不变，故无通知。</summary>
    public bool IsOdd { get; }

    /// <summary>共享列宽对象（行模板 4 个 <c>ColumnDefinition.Width</c> 绑它）。</summary>
    public TableColumnLayout Columns => _columns;

    /// <summary>「当前位置」列。</summary>
    public string Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    /// <summary>
    /// 「转移目标」列。
    /// </summary>
    /// <remarks>
    /// 必须是"有通知"的可写属性（架构 §7.3 硬前提 3 清单之一）：勾选、换目标根、
    /// 切换"创建当前用户目录"都会改变它，且这是用户判断"要搬到哪"的唯一依据。
    /// </remarks>
    public string Target
    {
        get => _target;
        private set => SetProperty(ref _target, value);
    }

    /// <summary>「状态」列（不裁字，宽度受列宽算法保护）。</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>行首勾选框（<c>TwoWay</c>）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value))
            {
                return;
            }

            // 勾选状态直接决定底色（未勾选 = Disabled），必须跟着刷新。
            RaisePropertyChanged(nameof(RowState));
        }
    }

    /// <summary>
    /// 行内勾选框是否可操作。
    /// </summary>
    /// <remarks>
    /// 复制一份"可编辑"到行上，是为了让行模板<b>只</b>用 <c>{Binding}</c>：
    /// 若写成 <c>{Binding DataContext.IsInputEnabled, ElementName=FolderList}</c>，就引入了
    /// 模板外的命名引用——这违反架构 §7.3 硬前提 2，而且一旦模板被复用到第二个列表上
    /// 就会静默失效（名字找不到不报错，只是永远不可编辑或永远可编辑）。
    /// </remarks>
    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (SetProperty(ref _isEditable, value))
            {
                RaisePropertyChanged(nameof(CanSelect));
            }
        }
    }

    /// <summary>
    /// 当前位置与目标位置是否同一路径（即状态列的「路径相同，无需转移」）。
    /// </summary>
    /// <remarks>
    /// 绑定源是 Core 的判定结果（<see cref="PlanItem.Same"/>），而不是在这里再比一次字符串——
    /// 比较规则（大小写、尾斜杠、环境变量展开）属于 Core，重复实现必然两边不一致。
    /// </remarks>
    public bool IsSameAsCurrent
    {
        get => _same;
        private set
        {
            if (SetProperty(ref _same, value))
            {
                RaisePropertyChanged(nameof(CanSelect));
            }
        }
    }

    /// <summary>
    /// 行首勾选框是否可勾（阶段允许 <b>且</b> 这一行确实有东西可搬）。
    /// </summary>
    /// <remarks>
    /// 「路径相同」的行勾上也没有意义：引擎会直接跳过它，而界面却显示"已选择 7 / 7 项"、
    /// 主按钮亮着——三个信号互相矛盾。这里把不可操作的行直接禁掉，勾选数才能等于待办数。
    /// </remarks>
    public bool CanSelect => _isEditable && !_same;

    /// <summary>「转移目标」列在与当前位置相同时的占位文案（弱化显示）。</summary>
    public string SameTargetText => UiText.SameTargetPlaceholder;

    /// <summary>
    /// 这一行的勾选是否是被"路径相同"规则自动取消的（内部状态，不参与绑定）。
    /// </summary>
    /// <remarks>
    /// 用户换了目标盘、该行重新变得可转移时，要把它自动勾回来——否则用户得手动重新勾一遍，
    /// 而他从没主动取消过任何东西。用户手动取消的行不会被置这个标记，因此不会被"复活"。
    /// </remarks>
    public bool AutoDeselected { get; set; }

    /// <summary>
    /// 上一轮执行结论（<c>null</c> = 从未执行过）。
    /// </summary>
    /// <remarks>
    /// 存在的唯一理由是回答"这一项还能不能再执行一次"：完成（<see cref="ItemStatus.Ok"/>）与
    /// 跳过（<see cref="ItemStatus.Skipped"/>）的项再执行一次，只会得到"当前位置不存在"的失败，
    /// 把一次成功的结果染红。失败与取消的项必须允许重试，所以不能简单地"执行过就排除"。
    /// </remarks>
    public ItemStatus? LastStatus { get; private set; }

    /// <summary>这一项是否还值得再执行一次（未被真正落实过）。</summary>
    public bool IsActionable => LastStatus is not (ItemStatus.Ok or ItemStatus.Skipped);

    /// <summary>
    /// 行着色状态。
    /// </summary>
    /// <remarks>
    /// 优先级固定：未勾选恒为 <see cref="RowState.Disabled"/>（用户已经明确"不处理这一项"，
    /// 继续用绿色/红色着色会误导他以为它还是本次操作的一部分），
    /// 其次才是上一次执行结果（<c>_transient</c>），最后回落到斑马纹。
    /// </remarks>
    public RowState RowState
    {
        get
        {
            if (!_isSelected)
            {
                return RowState.Disabled;
            }

            if (_transient.HasValue)
            {
                return _transient.Value;
            }

            return IsOdd ? RowState.Alt : RowState.Normal;
        }
    }

    /// <summary>
    /// 用一条规划结果整行刷新（零读盘：<see cref="PlanItem"/> 是内存态）。
    /// </summary>
    /// <param name="item">规划项。</param>
    /// <remarks>
    /// 一次性刷新的意义：上一次执行结果的着色只保留到<b>下次刷新</b>为止（PRD §4.1-10），
    /// 所以这里顺手把 <c>_transient</c> 清掉，而不是让每个调用方自己记得清。
    /// </remarks>
    public void Apply(PlanItem item) => Apply(item, resetOutcome: true);

    /// <summary>
    /// 用一条规划结果刷新，可选择<b>保留</b>上一次执行结果的着色与状态列文案。
    /// </summary>
    /// <param name="item">规划项。</param>
    /// <param name="resetOutcome">
    /// <c>true</c>：状态列重算、着色回落（用户主动触发的刷新）；
    /// <c>false</c>：只更新「当前位置 / 转移目标」，保留完成/失败着色（执行结束后自动刷新用）。
    /// </param>
    /// <remarks>
    /// 为什么要这个开关：执行结束后必须重新取一次计划（源已经搬走，旧计划里的 <c>Current</c> 已失效，
    /// 不重取的话用户再点一次主按钮会去搬一个不存在的源），但重取计划若顺手把着色清掉，
    /// 「完成/失败」的颜色会在用户眼皮底下瞬间消失，与 PRD §4.1-10「保留到下次刷新」相反。
    /// </remarks>
    public void Apply(PlanItem item, bool resetOutcome)
    {
        ArgumentNullException.ThrowIfNull(item);

        Current = item.Current ?? string.Empty;
        Target = item.Target ?? string.Empty;
        IsSameAsCurrent = item.Same;

        if (resetOutcome)
        {
            Status = item.Describe();
            SetTransient(null);
            LastStatus = null;
        }
    }

    /// <summary>把一行标成"本次将转移"（确认之后、执行之前）。</summary>
    public void MarkPending() => SetTransient(RowState.Pending);

    /// <summary>把一行标成"处理中"（项开始时由观察者回调）。</summary>
    public void MarkActive() => SetTransient(RowState.Active);

    /// <summary>
    /// 写入一项的执行结论：只改 <see cref="RowState"/> 与 <see cref="Status"/>。
    /// </summary>
    /// <param name="outcome">结论。</param>
    /// <remarks>
    /// 不在这里重设 <see cref="Target"/>：执行完目标已经落地，再用旧计划覆盖它会让
    /// 「转移目标」列显示执行前的值，而这个值此刻已经等于 <see cref="Current"/>，看起来像"没搬"。
    /// </remarks>
    public void ApplyOutcome(ItemOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        // 失败原因必须逐项可见（PRD §6.4），所以有 Message 就优先展示它，
        // 只给一句"失败"等于把用户推向日志文件。
        Status = string.IsNullOrWhiteSpace(outcome.Message) ? outcome.Label : outcome.Message;
        LastStatus = outcome.Status;

        SetTransient(outcome.Status switch
        {
            ItemStatus.Ok => RowState.Done,
            ItemStatus.Failed => RowState.Failed,
            ItemStatus.Cancelled => RowState.Disabled,
            ItemStatus.Skipped => RowState.Disabled,
            _ => RowState.Pending,
        });
    }

    /// <summary>清掉执行结果着色（回落到斑马纹）。</summary>
    public void ClearOutcome() => SetTransient(null);

    private void SetTransient(RowState? state)
    {
        if (_transient == state)
        {
            return;
        }

        _transient = state;
        RaisePropertyChanged(nameof(RowState));
    }
}
