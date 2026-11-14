using System;
using System.Windows;
using DirMover.App.ViewModels;
using DirMover.Core.Layout;
using Microsoft.UI.Dispatching;

namespace DirMover.App.Views;

/// <summary>
/// 表头与所有行<b>共享</b>的列宽对象（WPF 版）。
/// </summary>
/// <remarks>
/// 与 WinUI 版的算法逐行一致，差异只有两处类型替换：
/// <c>Microsoft.UI.Xaml.GridLength</c> → <c>System.Windows.GridLength</c>；
/// <c>DispatcherQueueTimer</c> → 本工程的同形封装（Compat/WinUiThreadShim.cs）。
/// 之所以不抽公共基类：两边唯一的公共部分就是"调 ColumnFitCalculator"，抽出来只会多一层间接。
/// <para>
/// 为什么 4 个 Width 属性每次 get 都返回新 <see cref="GridLength"/>：它是 struct，
/// PropertyChanged 只通知"重读该属性"，若返回同一个装箱实例，绑定层比较后认为"没变"而跳过刷新。
/// </para>
/// <para>
/// 为什么要高水位（只增不减，直到显式 <see cref="ResetHighWater"/>）：复制目录时文件名逐条出现，
/// 每来一个更短的名字就把列压窄，下一个长名字又撑开，视觉上就是持续抖动。
/// </para>
/// </remarks>
public sealed class TableColumnLayout : ObservableObject
{
    /// <summary>行首勾选列 + 右侧留白占用的固定宽度（DIP）。</summary>
    private const double SelectionColumnAndPadding = 40;

    /// <summary>重算防抖间隔：连续 SizeChanged / 文本变化只重算一次。</summary>
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
    /// <param name="dispatcher">UI 线程调度器（40 ms 防抖）；为空时退化为立即重算。</param>
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

        // 首帧必须已有合理宽度：等 SizeChanged 再来分配的话，第一帧四列宽度都是 0。
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

    /// <summary>文本区可用宽度（传入列表的原始宽度，勾选列的扣减在这里统一做一次）。</summary>
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

    /// <summary>提交本帧测量到的"各列完整显示所需宽度"。</summary>
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

    /// <summary>清掉高水位，允许列宽变窄（只在文本整体语境变了时调用）。</summary>
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

        _timer.Stop();
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object? args)
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
