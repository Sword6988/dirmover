using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DirMover.Core;

namespace DirMover.Wpf.Dialogs;

/// <summary>弹窗上用户按下的那颗按钮。</summary>
internal enum DialogButton
{
    /// <summary>未选择（关窗 / ESC）。</summary>
    None,

    /// <summary>主按钮（"是" / "回滚" / "打开日志目录"）。</summary>
    Primary,

    /// <summary>次按钮（"否" / "忽略"）。</summary>
    Secondary,

    /// <summary>关闭按钮（"确定" / "取消"）。</summary>
    Close,
}

/// <summary>
/// 通用弹窗窗口：标题 + 排版好的正文 + 1~3 颗按钮。
/// </summary>
/// <remarks>
/// 按钮的语义固定为"主 / 次 / 关闭"三档，与 <see cref="DialogButton"/> 一一对应；
/// 调用方只决定"放哪几句文案"，不决定布局——14 类弹窗的间距与字号因此天然一致。
/// </remarks>
public partial class DialogWindow : Window
{
    private Button? _primary;
    private Button? _secondary;

    /// <summary>构造。</summary>
    public DialogWindow()
    {
        InitializeComponent();

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>用户的选择。</summary>
    internal DialogButton Result { get; private set; }

    /// <summary>
    /// 装配弹窗内容。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="body">正文（通常是 DialogLayout 的产物）。</param>
    /// <param name="primaryText">主按钮文案；null 表示不显示。</param>
    /// <param name="secondaryText">次按钮文案；null 表示不显示。</param>
    /// <param name="closeText">关闭按钮文案；null 表示不显示。</param>
    /// <param name="width">内容宽度（正文阅读宽度）。</param>
    internal void Configure(
        string title,
        UIElement body,
        string? primaryText,
        string? secondaryText,
        string? closeText,
        double width)
    {
        Title = title;
        TitleText.Text = title;
        BodyHost.Content = body;
        // ScrollViewer 定宽：正文在固定阅读宽度内换行/滚动，窗口宽度由 SizeToContent 跟随。
        BodyScroll.Width = width;
        BodyScroll.MaxHeight = 440;
        BodyScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;

        if (closeText is not null)
        {
            Button close = CreateButton(closeText, "DefaultButtonStyle");
            close.Click += (_, _) => Finish(DialogButton.Close);
            ButtonBar.Children.Add(close);
        }

        if (secondaryText is not null)
        {
            _secondary = CreateButton(secondaryText, "DefaultButtonStyle");
            _secondary.Margin = new Thickness(8, 0, 0, 0);
            _secondary.Click += (_, _) => Finish(DialogButton.Secondary);
            ButtonBar.Children.Add(_secondary);
        }

        if (primaryText is not null)
        {
            _primary = CreateButton(primaryText, "PrimaryActionButtonStyle");
            _primary.Margin = new Thickness(8, 0, 0, 0);
            _primary.Click += (_, _) => Finish(DialogButton.Primary);
            ButtonBar.Children.Add(_primary);
        }
    }

    private static Button CreateButton(string text, string styleKey)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 84,
            Style = Application.Current.TryFindResource(styleKey) as Style,
        };

        return button;
    }

    private void Finish(DialogButton result)
    {
        Result = result;

        // ShowDialog 的返回值只为让调用方脱离模态循环；真正的语义在 Result 里。
        DialogResult = result != DialogButton.None;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Finish(DialogButton.Close);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            // 主按钮优先（"是"）；只有关闭键时回车=确定。
            if (_primary is not null)
            {
                Finish(DialogButton.Primary);
            }
            else
            {
                Finish(DialogButton.Close);
            }

            e.Handled = true;
        }
    }
}
