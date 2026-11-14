using DirMover.App.Dialogs;
using DirMover.App.ViewModels;
using DirMover.Core;
using DirMover.Core.Engine;
using DirMover.Core.IO;
using DirMover.Core.Layout;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace DirMover.App;

/// <summary>
/// 主窗口 code-behind：只做"宿主"该做的事（架构 §7.6）。
/// </summary>
/// <remarks>
/// 这里刻意<b>不</b>含任何业务判定：路径怎么拼、空间够不够、注册表写哪个值，全在 Core；
/// 按钮该不该亮、显示哪句话，全在 <see cref="MainViewModel"/>。本文件只剩四类纯机械动作：
/// ① 把 <c>DataContext</c> 接到根元素上；② 把 <c>SizeChanged</c> 翻译成列宽输入；
/// ③ 把键盘事件翻译成命令调用；④ 把窗口关闭翻译成"等 VM 收尾"。
/// </remarks>
public sealed partial class MainWindow : Window
{
    /// <summary>窗口最小宽度（DIP）：再窄会让名称列与状态列同时被压缩到不可读。</summary>
    private const int MinWidthDips = 780;

    /// <summary>窗口最小高度（DIP）。</summary>
    private const int MinHeightDips = 640;

    /// <summary>默认宽度（DIP）。</summary>
    private const int DefaultWidthDips = 900;

    /// <summary>
    /// 默认高度（DIP）。
    /// </summary>
    /// <remarks>
    /// 700→760：表格卡片化（卡片外边距 + 内边距共 48 DIP）与空态留白放大后，
    /// 7 行（294 DIP）在 700 高度下放不下，第 7 行会被视口裁掉半行。
    /// 这是布局改动的联动补偿，各间距令牌本身不动；最小高度 640 不变，
    /// 窗口压到下限时列表照常滚动（与改版前一致）。
    /// </remarks>
    private const int DefaultHeightDips = 760;

    private MainViewModel? _vm;
    private bool _allowClose;

    /// <summary>构造窗口。数据绑定在 <see cref="Attach"/> 里接。</summary>
    public MainWindow()
    {
        InitializeComponent();

        // 标题由系统标题栏承担，正文不重复标题（PRD §3 单屏布局）。
        Title = UiText.WindowTitle;

        Root.Loaded += OnRootLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        ApplyWindowIcon();
    }

    /// <summary>
    /// 显式设置窗口图标（标题栏 / 任务栏共用）。
    /// WinUI 的默认标题栏图标是系统通用小窗图形，<b>不会</b>自动取 exe 内嵌图标，
    /// 不设置会导致「exe 文件图标、任务栏图标、标题栏图标」三处不一致。
    /// 这里从程序集嵌入资源读 app.ico（与 csproj ApplicationIcon 同一份文件，见 csproj
    /// EmbeddedResource 注释）落临时文件后调用 <see cref="AppWindow.SetIcon(string)" />。
    /// </summary>
    private void ApplyWindowIcon()
    {
        const string ResourceName = "DirMover.App.Assets.app.ico";

        try
        {
            using var source = typeof(MainWindow).Assembly.GetManifestResourceStream(ResourceName);
            if (source is null)
            {
                // 资源缺失只退回系统默认图标，不能让窗口启动失败。
                return;
            }

            var directory = Path.Combine(Path.GetTempPath(), "DirMover");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "window-icon.ico");
            using (var file = File.Create(path))
            {
                source.CopyTo(file);
            }

            AppWindow.SetIcon(path);
        }
        catch (IOException)
        {
            // 图标是纯观感项，临时文件被占用等 IO 问题直接放弃，不影响功能。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>把视图模型接到根元素上（必须显式赋 <c>DataContext</c>，WinUI 不沿 Window 继承）。</summary>
    /// <param name="viewModel">主视图模型。</param>
    internal void Attach(MainViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        // 硬前提 1：不写这一行，整页 {Binding} 全部静默失败且没有任何异常。
        Root.DataContext = _vm;

        _vm.CloseRequested += (_, _) => Close();
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        ApplyInitialSize();
    }

    /// <summary>
    /// 按当前 DPI 设置初始尺寸，并夹住下限。
    /// </summary>
    /// <remarks>
    /// <c>AppWindow.Resize</c> 收的是<b>物理像素</b>，而我们定义的是 DIP。
    /// 不乘 <c>RasterizationScale</c> 的话，150% 缩放下窗口会只有设定值的 2/3，
    /// 第一次打开就像"窗口没记住大小"。
    /// </remarks>
    private void ApplyInitialSize()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(DefaultWidthDips * scale),
            (int)Math.Round(DefaultHeightDips * scale)));

        EnforceMinimumSize(scale);
    }

    /// <summary>
    /// 把窗口尺寸夹到下限。
    /// </summary>
    /// <remarks>
    /// 用 <c>AppWindow.Changed</c> + 回夹，而不是 <c>OverlappedPresenter.PreferredMinimumWidth</c>：
    /// 后者是较新才补齐的成员，在部分 WindowsAppSDK 版本上不存在；回夹虽然会闪一帧，
    /// 但它在任何版本上都编译得过、也不依赖 presenter 的具体类型。
    /// </remarks>
    private void EnforceMinimumSize(double scale)
    {
        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange)
            {
                return;
            }

            SizeInt32 current = AppWindow.Size;
            int minWidth = (int)Math.Round(MinWidthDips * scale);
            int minHeight = (int)Math.Round(MinHeightDips * scale);

            if (current.Width >= minWidth && current.Height >= minHeight)
            {
                return;
            }

            AppWindow.Resize(new SizeInt32(
                Math.Max(current.Width, minWidth),
                Math.Max(current.Height, minHeight)));
        };
    }

    /// <summary>
    /// 列表尺寸变化 → 列宽重算入口。
    /// </summary>
    /// <remarks>
    /// 写入的是<b>原始</b>视口宽度；勾选列与留白的扣减统一由
    /// <see cref="Views.TableColumnLayout.AvailableWidth"/> 做一次，
    /// 避免表头与行模板各扣一次导致错位。
    /// </remarks>
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
    /// 需要量的是<b>表头</b>与每行文本的最大值，而不是当前可见行——否则一次滚动就会让列宽跳动。
    /// 7 行 × 4 列的 <c>Measure</c> 是微秒级操作，直接全量量即可，不必做缓存失效。
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
            needs[i] = MeasureWidth(headers[i]) + ColumnFitCalculator.HeadPad;
        }

        foreach (FolderRowViewModel row in _vm.Rows)
        {
            Widen(needs, 0, MeasureWidth(row.Display));
            Widen(needs, 1, MeasureWidth(row.Current));
            Widen(needs, 2, MeasureWidth(row.Target));
            Widen(needs, 3, MeasureWidth(row.Status));
        }

        // 状态列不再有圆点（2026-11-14 定稿），纯文字测量即真实所需宽度，
        // 不需要任何固定补偿量。改动见行模板状态列注释。
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

    /// <summary>用一次性 <see cref="TextBlock"/> 量文本宽度（DIP）。</summary>
    /// <param name="text">文本。</param>
    private static int MeasureWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var probe = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
        };

        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return (int)Math.Ceiling(probe.DesiredSize.Width);
    }

    /// <summary>
    /// 表格键盘：空格/回车切换勾选，<c>Ctrl+Enter</c> 不切换（那条归加速器管主操作）。
    /// </summary>
    /// <remarks>
    /// 必须显式处理：<c>ListView</c> 的空格默认语义是"切换 SelectedItem"，
    /// 而本界面 <c>SelectionMode=None</c>，按键会穿透且什么都不发生——用户会以为键盘坏了。
    /// </remarks>
    private void OnFolderListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl)
        {
            // Ctrl+Enter 放行给 KeyboardAccelerator，这里不碰勾选。
            return;
        }

        if (e.Key != VirtualKey.Space && e.Key != VirtualKey.Enter)
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

    private void OnPrimaryAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_vm is null)
        {
            return;
        }

        if (_vm.PrimaryCommand.CanExecute(null))
        {
            _vm.PrimaryCommand.Execute(null);
        }

        args.Handled = true;
    }

    /// <summary>
    /// 关窗收尾（架构 §7.6 六行伪码）。
    /// </summary>
    /// <remarks>
    /// 一律先 <c>Cancel = true</c> 再走统一路径，而不是"空闲就直接关"：
    /// 空闲只是当前判断，收尾过程中状态还会变；两条路径会分叉出"空闲时关窗不 flush 日志"这种问题。
    /// 也<b>不</b>在 <c>Closing</c> 里调 <c>Environment.Exit</c>——那会跳过日志 flush，
    /// 用户恰恰在最需要日志的时候拿不到日志。
    /// </remarks>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;

        if (_vm is null)
        {
            _allowClose = true;
            Close();
            return;
        }

        if (!await _vm.RequestCloseAsync())
        {
            return;
        }

        _allowClose = true;
        Close();
    }
}
