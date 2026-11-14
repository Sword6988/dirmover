using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DirMover.App.Dialogs;

/// <summary>
/// 弹窗内容的统一排版（正文段落 / 等宽报告 / 响应式尺寸）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这一层：<see cref="ContentDialog.Content"/> 直接塞字符串时，文本由
/// <c>ContentPresenter</c> 自动合成一个 <see cref="TextBlock"/>，行高、段间距、换行宽度
/// 都不受控；多段正文只能靠空行硬撑（空行 = 一个 14px 行高的空 TextBlock），
/// 长内容（失败清单、扫描报告）还会顶破弹窗——<c>ContentDialog</c> 只给内容区
/// 有限的内部滚动，裸字符串连滚动都进不去。
/// </para>
/// <para>
/// 所以正文一律先经本类加工：按空行切成"段落"，每段一个 TextBlock，
/// 段间距由 <c>StackPanel.Spacing</c> 统一给（而非空行），
/// 尺度全部取自 Tokens 的 <c>Dialog*</c> 令牌与 Styles 的 <c>Dialog*</c> 样式——
/// 本文件不出现字号、颜色、间距字面量。
/// </para>
/// <para>
/// 零文案：本类只摆字符串，标题/正文/按钮一律由调用方从 <see cref="Core.UiText"/> 取。
/// </para>
/// </remarks>
internal static class DialogLayout
{
    /// <summary>段落分隔符（空行）：兼容 <see cref="Environment.NewLine"/> 与显式 "\n" 两种写法。</summary>
    private static readonly string[] ParagraphSeparators = { "\r\n\r\n", "\n\n", "\r\r" };

    /// <summary>弹窗左右非内容宽度（弹窗内边距 + 遮罩留白）的估算值。</summary>
    private const double ChromeWidth = 160;

    /// <summary>弹窗上下非内容高度（标题行 + 按钮条 + 内边距）的估算值。</summary>
    private const double ChromeHeight = 240;

    /// <summary>正文宽度下限（低于此值中文一行放不下 10 个字，宁可让弹窗比窗口略宽也不要挤成柱状）。</summary>
    private const double MinBodyWidth = 240;

    /// <summary>正文高度下限（至少露出 5 行）。</summary>
    private const double MinBodyHeight = 120;

    /// <summary>
    /// 把正文文本加工成"段落栈"：空行切段、段内换行保留、行高与段间距取自令牌。
    /// </summary>
    /// <param name="text">正文（可含多段，段间以空行分隔）。</param>
    /// <param name="highlightLastParagraph">是否把最后一段按危险色呈现（失败清单用）。</param>
    /// <returns>可直接作为 <see cref="ContentDialog.Content"/> 的段落容器。</returns>
    internal static StackPanel CreateBody(string? text, bool highlightLastParagraph = false)
    {
        var stack = new StackPanel { Style = ResolveStyle("DialogBodyStackStyle") };

        List<string> paragraphs = SplitParagraphs(text);
        for (int i = 0; i < paragraphs.Count; i++)
        {
            bool danger = highlightLastParagraph && i == paragraphs.Count - 1;
            stack.Children.Add(new TextBlock
            {
                Style = ResolveStyle(danger ? "DialogBodyDangerTextStyle" : "DialogBodyTextStyle"),
                Text = paragraphs[i],
            });
        }

        return stack;
    }

    /// <summary>
    /// 把等宽报告文本加工成"浅灰卡片 + 双向滚动容器"。
    /// </summary>
    /// <param name="text">报告正文（列对齐，禁止折行）。</param>
    /// <returns>可直接作为 <see cref="ContentDialog.Content"/> 的滚动容器。</returns>
    internal static ScrollViewer CreateMonoBody(string? text)
    {
        var surface = new Border
        {
            Style = ResolveStyle("DialogMonoSurfaceStyle"),
            Child = new TextBlock
            {
                Style = ResolveStyle("DialogMonoTextStyle"),
                Text = text ?? string.Empty,
            },
        };

        return new ScrollViewer
        {
            Style = ResolveStyle("DialogMonoScrollStyle"),
            Content = surface,
        };
    }

    /// <summary>
    /// 按窗口可用尺寸收紧内容区上限（小窗口下不顶破弹窗，大窗口下不放宽阅读宽度）。
    /// </summary>
    /// <param name="host">正文容器（<see cref="CreateBody"/> 或 <see cref="CreateMonoBody"/> 的返回值）。</param>
    /// <param name="available">窗口内容区尺寸（<c>XamlRoot.Size</c>）。</param>
    /// <remarks>
    /// 只做"向下收"：上限的默认值写在 Styles 的 <c>Dialog*Style</c> 里（480×440），
    /// 这里仅在窗口更小时压低它——两处不重复定义同一个上限值。
    /// 窗口最小尺寸是 780×640，640 高度扣掉标题与按钮条后正文仍有约 400，够 7 行目录清单。
    /// </remarks>
    internal static void FitToWindow(FrameworkElement host, Size available)
    {
        if (available.Width > 0)
        {
            double width = available.Width - ChromeWidth;
            if (width < host.MaxWidth)
            {
                host.MaxWidth = Math.Max(MinBodyWidth, width);
            }
        }

        if (available.Height > 0)
        {
            double height = available.Height - ChromeHeight;
            if (height < host.MaxHeight)
            {
                host.MaxHeight = Math.Max(MinBodyHeight, height);
            }
        }
    }

    /// <summary>
    /// 按空行切段：去掉每段首尾空白与空段，段内换行归一成 <c>\n</c>。
    /// </summary>
    /// <param name="text">原始正文。</param>
    /// <returns>段落列表（可能为空）。</returns>
    private static List<string> SplitParagraphs(string? text)
    {
        var paragraphs = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return paragraphs;
        }

        foreach (string raw in text.Split(ParagraphSeparators, StringSplitOptions.None))
        {
            // 段内换行统一成 \n：TextBlock 对 \n 与 \r\n 都能换行，但混用会在测量时
            // 多出一个不可见字符的宽度，包住长路径时容易多折一行。
            string normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (normalized.Length > 0)
            {
                paragraphs.Add(normalized);
            }
        }

        return paragraphs;
    }

    /// <summary>从应用级资源取样式（缺失时返回 <c>null</c>，保持默认样式而不是抛）。</summary>
    /// <param name="key">样式键。</param>
    /// <returns>样式实例或 <c>null</c>。</returns>
    private static Style? ResolveStyle(string key) =>
        Application.Current?.Resources is ResourceDictionary resources
        && resources.TryGetValue(key, out object? found)
        && found is Style style
            ? style
            : null;
}
