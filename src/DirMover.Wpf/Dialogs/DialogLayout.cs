using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace DirMover.Wpf.Dialogs;

/// <summary>
/// 弹窗内容的统一排版（正文段落 / 等宽报告 / 响应式尺寸），WPF 版。
/// </summary>
/// <remarks>
/// 规则与 WinUI 版完全一致：按空行切段、每段一个元素、段间距由排版给（而非空行硬撑）、
/// 尺度全部取自 Tokens 的 <c>Dialog*</c> 令牌。
/// 一处平台差异：WPF 的 TextBlock 不支持文本选中（WinUI 的 IsTextSelectionEnabled 无对应物），
/// 长报告因此改用只读 TextBox（可选中、可复制），正文段落仍是 TextBlock。
/// </remarks>
internal static class DialogLayout
{
    /// <summary>段落分隔符（空行）：兼容 CRLF 与 LF 两种写法。</summary>
    private static readonly string[] ParagraphSeparators = { "\r\n\r\n", "\n\n", "\r\r" };

    /// <summary>弹窗左右非内容宽度（窗口外边距 + 内边距）的估算值。</summary>
    private const double ChromeWidth = 160;

    /// <summary>弹窗上下非内容高度（标题行 + 按钮条 + 内边距）的估算值。</summary>
    private const double ChromeHeight = 240;

    /// <summary>正文宽度下限（低于此值中文一行放不下 10 个字）。</summary>
    private const double MinBodyWidth = 240;

    /// <summary>正文高度下限（至少露出 5 行）。</summary>
    private const double MinBodyHeight = 120;

    /// <summary>把正文加工成"段落栈"：空行切段、段内换行保留。</summary>
    /// <param name="text">正文（可含多段，段间以空行分隔）。</param>
    /// <param name="highlightLastParagraph">是否把最后一段按危险色呈现（失败清单用）。</param>
    internal static StackPanel CreateBody(string? text, bool highlightLastParagraph = false)
    {
        var stack = new StackPanel();
        List<string> paragraphs = SplitParagraphs(text);

        for (int i = 0; i < paragraphs.Count; i++)
        {
            bool danger = highlightLastParagraph && i == paragraphs.Count - 1;
            stack.Children.Add(new TextBlock
            {
                Style = Resolve<Style>(danger ? "DialogBodyDangerTextStyle" : "DialogBodyTextStyle"),
                Text = paragraphs[i],
                Margin = i == 0 ? new Thickness(0) : new Thickness(0, ParagraphSpacing, 0, 0),
            });
        }

        return stack;
    }

    /// <summary>把等宽报告加工成"浅灰卡片 + 可选中可滚动"的块。</summary>
    /// <param name="text">报告正文（列对齐，禁止折行）。</param>
    /// <param name="width">阅读宽度（卡片随之铺满正文区，而不是缩成文本自然宽度再居中）。</param>
    internal static Border CreateMonoBody(string? text, double width)
    {
        double padH = 24; // DialogMonoPadding 的左右分量（12 + 12）
        var surface = new Border
        {
            Style = Resolve<Style>("DialogMonoSurfaceStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var box = new TextBox
        {
            Style = Resolve<Style>("DialogMonoTextStyle"),
            Text = text ?? string.Empty,
            Width = Math.Max(MinBodyWidth, width - padH),
            TextAlignment = TextAlignment.Left,
            // 显式再设一遍：报告是列对齐文本，折行即毁对齐（样式里也有，这里防样式缺失兜底）。
            TextWrapping = TextWrapping.NoWrap,
            IsReadOnlyCaretVisible = false,
        };

        surface.Child = box;
        return surface;
    }

    /// <summary>按窗口可用尺寸收紧内容区上限（小窗口下不顶破弹窗，大窗口下不放宽阅读宽度）。</summary>
    /// <param name="host">正文容器。</param>
    /// <param name="available">窗口内容区尺寸。</param>
    internal static void FitToWindow(FrameworkElement host, Size available)
    {
        double maxWidth = Resolve<double>("DialogContentMaxWidth", 480);
        double maxHeight = Resolve<double>("DialogContentMaxHeight", 440);

        host.MaxWidth = available.Width > 0
            ? Math.Max(MinBodyWidth, Math.Min(maxWidth, available.Width - ChromeWidth))
            : maxWidth;

        host.MaxHeight = available.Height > 0
            ? Math.Max(MinBodyHeight, Math.Min(maxHeight, available.Height - ChromeHeight))
            : maxHeight;
    }

    /// <summary>当前正文的显示宽度（供 DialogWindow 设置窗口宽度）。</summary>
    internal static double PreferredWidth()
    {
        return Resolve<double>("DialogContentMaxWidth", 480);
    }

    /// <summary>按空行切段：去掉每段首尾空白与空段，段内换行归一成 <c>\n</c>。</summary>
    private static List<string> SplitParagraphs(string? text)
    {
        var paragraphs = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return paragraphs;
        }

        foreach (string raw in text.Split(ParagraphSeparators, StringSplitOptions.None))
        {
            string normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (normalized.Length > 0)
            {
                paragraphs.Add(normalized);
            }
        }

        return paragraphs;
    }

    private static double ParagraphSpacing => Resolve<double>("DialogParagraphSpacing", 10);

    private static T? Resolve<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T;

    private static T Resolve<T>(string key, T fallback) =>
        Application.Current?.TryFindResource(key) is T found ? found : fallback;
}
