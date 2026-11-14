using DirMover.App.ViewModels;
using DirMover.Core.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DirMover.App.Views;

/// <summary>
/// 表头与所有行<b>共享</b>的列宽对象（架构 §7.3）。
/// </summary>
/// <remarks>
/// 为什么必须共享一份而不是每行各算：7 行 × 4 列的 <c>ColumnDefinition.Width</c> 若各自绑定，
/// 一次 <c>resize</c> 会触发 28 次独立重算，且各行测量先后不一，列宽会互相打架。
/// <para>
/// 为什么 4 个 <c>Width</c> 属性每次 <c>get</c> 都返回<b>新</b> <see cref="GridLength"/> 实例：
/// <see cref="GridLength"/> 是 <c>struct</c>，<c>PropertyChanged</c> 只是通知"重读该属性"，
/// 若属性返回的是同一个装箱实例，绑定层比较后认为"没变"而跳过刷新 —— 列宽就永远不动了。
/// </para>
/// <para>
/// 为什么要高水位（只增不减，直到显式 <see cref="ResetHighWater"/>）：复制目录时文件名逐条出现，
/// 每来一个更短的名字就把列压窄，下一个长名字又撑开，视觉上就是持续抖动。高水位把"变窄"推后到
/// 明确的"整体语境变了"（换目标根 / 换用户名目录 / 重新扫描）时再发生。
/// </para>
/// </remarks>
public sealed class TableColumnLayout : ObservableObject
{
    /// <summary>
    /// 行首勾选列 + 右侧留白占用的固定宽度（DIP）。
    /// </summary>
    /// <remarks>
    /// 来源：勾选列的固定内容宽 32 DIP，加上列表项右侧留白 8 DIP。这一列不参与列宽分配，
    /// 必须在算 4 个文本列之前从视口里扣掉——所以它<b>不是</b>一个"列宽令牌"，
    /// 而是一个"视口修正量"，刻意不放进 Tokens.xaml（那里放的是可设计调整的视觉值）。
    /// </remarks>
    private const double SelectionColumnAndPadding = 40;

    /// <summary>重算防抖间隔：连续 <c>SizeChanged</c>/文本变化只重算一次。</summary>
    public static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(40);

    private readonly DispatcherQueue? _dispatcher;
    private readonly int[] _needs;
    private readonly int[] _lastNeeds;
    private readonly int[] _minimums;
    private readonly int[] _quotas;
    private DispatcherQueueTimer? _timer;
    private int _viewport;
    private int _nameWidth;
    private int _currentWidth;
    private int _targetWidth;
    private int _statusWidth;

    /// <summary>构造。</summary>
    /// <param name="dispatcher">
    /// UI 线程调度器（用于 40 ms 防抖）。为空时退化为"立即重算"：
    /// 这样在没有消息泵的环境（设计期 / 单元测试）里也不会静默不刷新。
    /// </param>
    public TableColumnLayout(DispatcherQueue? dispatcher)
    {
        _dispatcher = dispatcher;

        int count = ColumnFitCalculator.Columns.Count;
        _needs = new int[count];
        _lastNeeds = new int[count];
        _minimums = new int[count];
        _quotas = new int[count];
        for (int i = 0; i < count; i++)
        {
            _minimums[i] = ColumnFitCalculator.MinColumn;
            _quotas[i] = ColumnFitCalculator.Columns[i].Quota;
        }

        _viewport = ColumnFitCalculator.FallbackViewport;

        // 首帧必须已有合理宽度：等 SizeChanged 再来分配的话，第一帧四列宽度都是 0，
        // 内容会挤成一条竖线，用户看到的是"表格没画出来"。
        Apply(_viewport);
    }

    /// <summary>「文件夹名称」列宽。</summary>
    public GridLength NameWidth => new(_nameWidth);

    /// <summary>「当前位置」列宽。</summary>
    public GridLength CurrentWidth => new(_currentWidth);

    /// <summary>「转移目标」列宽。</summary>
    public GridLength TargetWidth => new(_targetWidth);

    /// <summary>「状态」列宽。</summary>
    public GridLength StatusWidth => new(_statusWidth);

    /// <summary>
    /// 文本区可用宽度。
    /// </summary>
    /// <remarks>
    /// <c>set</c> 收的是 <c>ListView</c> 的<b>原始</b>宽度（<c>SizeChanged</c> 的 <c>NewSize.Width</c>），
    /// 勾选列的扣减在这里统一做一次。若让调用方自己扣，7 行模板和表头各扣一次就必然对不上。
    /// </remarks>
    public double AvailableWidth
    {
        get => _viewport;
        set
        {
            int width = (int)Math.Round(Math.Max(value - SelectionColumnAndPadding, 0));
            if (width == _viewport)
            {
                return;
            }

            _viewport = width;
            Apply(width);
            RaisePropertyChanged();
        }
    }

    /// <summary>
    /// 提交本帧测量到的"各列完整显示所需宽度"。
    /// </summary>
    /// <param name="needs">长度必须等于列数（4）；长度不符说明调用方量错了列，直接忽略。</param>
    public void UpdateNeeds(int[] needs)
    {
        if (needs is null || needs.Length != _needs.Length)
        {
            return;
        }

        for (int i = 0; i < needs.Length; i++)
        {
            int need = Math.Max(needs[i], 0);
            _lastNeeds[i] = need;

            // 高水位：只抬不落。这是"不抖动"的唯一保证点。
            if (need > _needs[i])
            {
                _needs[i] = need;
            }
        }

        Schedule();
    }

    /// <summary>
    /// 清掉高水位，允许列宽<b>变窄</b>。
    /// </summary>
    /// <remarks>
    /// 只在"文本整体语境变了"时调用（换目标根、换用户名目录、重新扫描）。
    /// 重置到<b>最近一次实测</b>而不是 0：归零会让列先缩到最小值再被撑回来，闪一下。
    /// </remarks>
    public void ResetHighWater()
    {
        Array.Copy(_lastNeeds, _needs, _needs.Length);
        Schedule();
    }

    private void Schedule()
    {
        if (_dispatcher is null)
        {
            Apply(_viewport);
            return;
        }

        if (_timer is null)
        {
            _timer = _dispatcher.CreateTimer();
            _timer.Interval = DebounceInterval;
            _timer.IsRepeating = false;
            _timer.Tick += OnTick;
        }

        // 每次提交都重置计时：连续 N 次变化合并成"最后一次之后 40 ms"的一次重算。
        _timer.Stop();
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        Apply(_viewport);
    }

    private void Apply(int available)
    {
        int[] widths = ColumnFitCalculator.Fit((int[])_needs.Clone(), _minimums, _quotas, available);
        if (widths.Length != _needs.Length)
        {
            return;
        }

        SetWidth(ref _nameWidth, widths[0], nameof(NameWidth));
        SetWidth(ref _currentWidth, widths[1], nameof(CurrentWidth));
        SetWidth(ref _targetWidth, widths[2], nameof(TargetWidth));
        SetWidth(ref _statusWidth, widths[3], nameof(StatusWidth));
    }

    private void SetWidth(ref int field, int value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        RaisePropertyChanged(propertyName);
    }
}
