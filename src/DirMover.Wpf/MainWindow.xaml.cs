using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DirMover.App.ViewModels;
using DirMover.Core;
using DirMover.Core.Layout;

namespace DirMover.Wpf;

/// <summary>
/// 主窗口 code-behind：只做"宿主"该做的事。
/// </summary>
/// <remarks>
/// 与 WinUI 版同一约定：这里不含任何业务判定（路径怎么拼、空间够不够、注册表写哪个值在 Core，
/// 按钮亮不亮、显示哪句话在 <see cref="MainViewModel"/>）。本文件只剩四类纯机械动作：
/// ① 把 DataContext 接到窗口；② 把 SizeChanged 翻译成列宽输入；
/// ③ 把键盘事件翻译成命令调用；④ 把关闭翻译成"等 VM 收尾"。
/// </remarks>
public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private bool _allowClose;

    /// <summary>构造窗口。数据绑定在 <see cref="Attach"/> 里接。</summary>
    public MainWindow()
    {
        InitializeComponent();

        // 标题由系统标题栏承担，正文不重复标题。
        Title = UiText.WindowTitle;
    }

    /// <summary>把视图模型接到窗口上。</summary>
    /// <param name="viewModel">主视图模型。</param>
    internal void Attach(MainViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        // WPF 的 DataContext 会沿可视树向下继承，赋在窗口上即可覆盖整页。
        DataContext = _vm;

        _vm.CloseRequested += (_, _) => Close();
    }

    /// <summary>列表尺寸变化 → 列宽重算入口（写入原始视口宽度，勾选列的扣减在 TableColumnLayout 里做一次）。</summary>
    private void OnFolderListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        _vm.Columns.AvailableWidth = e.NewSize.Width;
        RecomputeColumnNeeds();
    }

    /// <summary>
    /// 量出 4 列"完整显示所需宽度"并提交给列宽算法。
    /// </summary>
    /// <remarks>
    /// 量的是表头与每行文本的最大值，而不是当前可见行——否则一次滚动就会让列宽跳动。
    /// 7 行 × 4 列的测量是微秒级操作，直接全量量即可。
    /// </remarks>
    private void RecomputeColumnNeeds()
    {
        if (_vm is null)
        {
            return;
        }

        string[] headers =
        {
            _vm.ColumnNameHeader,
            _vm.ColumnCurrentHeader,
            _vm.ColumnTargetHeader,
            _vm.ColumnStatusHeader,
        };

        var needs = new int[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            needs[i] = MeasureWidth(headers[i], 12, FontWeights.SemiBold) + ColumnFitCalculator.HeadPad;
        }

        foreach (FolderRowViewModel row in _vm.Rows)
        {
            Widen(needs, 0, MeasureWidth(row.Display, 15, FontWeights.SemiBold));
            Widen(needs, 1, MeasureWidth(row.Current, 14, FontWeights.Normal));
            Widen(needs, 2, MeasureWidth(row.Target, 14, FontWeights.Normal));
            Widen(needs, 3, MeasureWidth(row.Status, 14, FontWeights.Normal));
        }

        _vm.Columns.UpdateNeeds(needs);
    }

    private static void Widen(int[] needs, int index, int width)
    {
        int candidate = width + ColumnFitCalculator.CellPad;
        if (candidate > needs[index])
        {
            needs[index] = candidate;
        }
    }

    /// <summary>
    /// 用 <see cref="FormattedText"/> 量文本宽度（DIP）。
    /// </summary>
    /// <remarks>
    /// 不用 <c>TextBlock.Measure</c>：脱离可视树的 TextBlock 拿不到窗口的字体设置，
    /// 量出来会偏小；FormattedText 可以显式指定字体族/字号/字重，结果与渲染一致。
    /// <c>pixelsPerDip</c> 固定 1.0：列宽算法的单位本来就是 DIP，与屏幕缩放无关。
    /// </remarks>
    private static int MeasureWidth(string? text, double fontSize, FontWeight weight)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var typeface = new Typeface(
            new FontFamily("Microsoft YaHei UI"),
            FontStyles.Normal,
            weight,
            FontStretches.Normal);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            pixelsPerDip: 1.0);

        return (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace);
    }

    /// <summary>表格键盘：空格/回车切换勾选，Ctrl+Enter 放行给输入绑定（主操作）。</summary>
    private void OnFolderListKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl)
        {
            return;
        }

        if (e.Key != Key.Space && e.Key != Key.Enter)
        {
            return;
        }

        if (FolderList.SelectedItem is FolderRowViewModel row)
        {
            _vm.ToggleRow(row);
        }
        else if (FolderList.Items.Count == 1 && FolderList.Items[0] is FolderRowViewModel only)
        {
            _vm.ToggleRow(only);
        }

        e.Handled = true;
    }

    /// <summary>
    /// 关窗收尾：一律先 <c>Cancel = true</c> 再走统一路径，让 VM 有机会确认/收尾。
    /// </summary>
    /// <remarks>
    /// 不在 <see cref="Window.Closing"/> 里调 <c>Environment.Exit</c>：那会跳过日志 flush，
    /// 而用户恰恰在最需要日志的时候拿不到日志。
    /// </remarks>
    private async void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;

        if (_vm is null)
        {
            CloseDeferred();
            return;
        }

        if (!await _vm.RequestCloseAsync())
        {
            return;
        }

        CloseDeferred();
    }

    /// <summary>
    /// 在 <see cref="Window.Closing"/> 管线之外补一次真正的 <see cref="Window.Close"/>。
    /// </summary>
    /// <remarks>
    /// WPF 禁止在关闭过程中再调 Show/ShowDialog/Close（Window.VerifyNotClosing）。
    /// <c>RequestCloseAsync</c> 的续体经 Dispatcher 恢复时，触发本次 Closing 的那次
    /// Close 调用可能还没走完，此时直接 <c>Close()</c> 会抛
    /// <c>InvalidOperationException</c>（2026-11-14 实测，日志 startup_error.txt）。
    /// Cancel 已经生效，把最终关闭延到下一轮 Dispatcher 即可 —— 那是一次全新的
    /// Close 调用，<c>_allowClose</c> 为 true，Closing 处理器直接放行。
    /// </remarks>
    private void CloseDeferred()
    {
        _allowClose = true;
        Dispatcher.BeginInvoke(new Action(Close));
    }
}
