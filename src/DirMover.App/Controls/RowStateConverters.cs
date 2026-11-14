using DirMover.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DirMover.App.Controls;

/// <summary>
/// <see cref="RowState"/> → 背景画刷。
/// </summary>
/// <remarks>
/// 转换器内部<b>只做查表</b>，不出现任何颜色字面量（架构 §7.4 红线）：
/// 颜色只在 <c>Theme/Tokens.xaml</c> 的 <c>ThemeDictionaries</c> 里定义一次，
/// 深色/浅色主题由系统切换字典，这里无需知情。
/// <para>
/// 资源缺失时必须返回透明画刷而不是抛异常：转换器抛出的异常会被 XAML 边界吞成
/// "整行渲染失败"，表现为表格空白，极难定位。宁可少一层底色，也不能让行消失。
/// </para>
/// </remarks>
public sealed class RowStateToBackgroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        TokenBrush.Background(TokenBrush.AsState(value));

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary><see cref="RowState"/> → 前景画刷。</summary>
public sealed class RowStateToForegroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        TokenBrush.Foreground(TokenBrush.AsState(value));

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// <see cref="RowState"/> → <b>状态列专用</b>前景画刷。
/// </summary>
/// <remarks>
/// 与 <see cref="RowStateToForegroundConverter"/>（整行正文）分离是刻意的：
/// 正文前景刻意收敛（见 <see cref="TokenBrush.ForegroundKey"/> 的注释，整行红字压制可读性），
/// 而状态列只有一小段短文案，让它按状态着色（处理中青 / 完成绿 / 失败红）是把
/// 已删除的"圆点通道"（2026-11-14 用户反馈移除）换成<b>零占位空间</b>的辨识通道——
/// 不占宽度、7 行同态时不成色块噪音，色弱用户仍有"处理中/完成/失败"文字语义兜底。
/// "无需转移"与"待转移"不参与着色：前者是弱灰（Disabled 语义），后者保持正文色
/// 最醒目（它是唯一的行动号召态，红绿反而会把它从扫视线里挤出去）。
/// </remarks>
public sealed class RowStateToStatusForegroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        TokenBrush.StatusForeground(TokenBrush.AsState(value));

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// <see cref="RowState"/> → 不透明度。
/// </summary>
/// <remarks>
/// 只用于未勾选行：底色已经是 muted，再压一点不透明度是为了让"这一项不参与本次操作"
/// 一眼可辨（PRD §4.1 的可读性要求）。不透明度不是颜色，也不是像素，故不放进令牌文件。
/// </remarks>
public sealed class RowStateToOpacityConverter : IValueConverter
{
    /// <summary>令牌键：未勾选行的整体不透明度。</summary>
    public const string DisabledOpacityKey = "DirMoverDisabledOpacity";

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        TokenBrush.AsState(value) == RowState.Disabled ? TokenBrush.Double(DisabledOpacityKey, 0.55) : 1.0;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary><c>bool</c> → <see cref="Visibility"/>（<c>true</c> 可见）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool flag && flag ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility visibility && visibility == Visibility.Visible;
}

/// <summary><c>bool</c> → <see cref="Visibility"/>（<c>true</c> 隐藏）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility visibility && visibility != Visibility.Visible;
}

/// <summary>
/// <c>bool</c> 取反。
/// </summary>
/// <remarks>
/// 架构 §7.1 的提权提示条绑定 <c>IsOpen={Binding IsElevated, Converter=InvertBool}</c>：
/// "未提权"才需要露出警告条，而 VM 侧的语义属性叫 <c>IsElevated</c>（正向命名更好读），
/// 于是取反这一步必须由转换器承担。这条是 §7.1 明确要求的，故单独实现。
/// </remarks>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not bool flag || !flag;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not bool flag || !flag;
}

/// <summary>行状态 → 令牌名 → 画刷的查表实现（三段分离，便于单元测试断言"名字"）。</summary>
internal static class TokenBrush
{
    /// <summary>背景令牌名（架构 §7.4 第三列）。</summary>
    /// <param name="state">行状态。</param>
    public static string BackgroundKey(RowState state) => state switch
    {
        RowState.Alt => "RowAltBrush",
        RowState.Pending => "PendingRowBrush",
        RowState.Active => "AccentSubtleBrush",
        RowState.Done => "SuccessSubtleBrush",
        RowState.Failed => "DangerSubtleBrush",
        _ => "SurfaceBrush",
    };

    /// <summary>前景令牌名（架构 §7.4 第四列）。</summary>
    /// <remarks>
    /// 行状态对正文前景的影响刻意收敛到一档：只有 <see cref="RowState.Disabled"/> 压灰。
    /// Failed 行正文仍用 <c>TextPrimaryBrush</c>（Phase 1 决策）：失败辨识交给
    /// 底色（DangerSubtleBrush）+ 状态列文案本身（"失败：原因"），
    /// 整行红字会压制正文可读性。
    /// 2026-11-14 起状态列不再画圆点（用户反馈），状态辨识完全由文字承担。
    /// </remarks>
    /// <param name="state">行状态。</param>
    public static string ForegroundKey(RowState state) => state switch
    {
        RowState.Disabled => "TextDisabledBrush",
        _ => "TextPrimaryBrush",
    };

    /// <summary>按背景令牌取画刷。</summary>
    /// <param name="state">行状态。</param>
    public static Brush Background(RowState state) => Resolve(BackgroundKey(state));

    /// <summary>按前景令牌取画刷。</summary>
    /// <param name="state">行状态。</param>
    public static Brush Foreground(RowState state) => Resolve(ForegroundKey(state));

    /// <summary>状态列专用前景令牌名（与正文前景分离，理由见 <see cref="StatusForeground"/>）。</summary>
    /// <remarks>
    /// 颜色档与行底色同族：Active 用 AccentTextBrush（青）、Done 用 SuccessTextBrush（绿）、
    /// Failed 用 DangerTextBrush（红），三者在其对应 Subtle 底色上的对比度均 ≥ 4.5:1。
    /// Disabled/Normal/Pending 刻意不做特殊着色——弱灰/正文色已是它们的正确语义，
    /// 再加颜色只会让"有颜色的状态"失去稀缺性（彩色一多就等于没有彩色）。
    /// </remarks>
    /// <param name="state">行状态。</param>
    public static string StatusForegroundKey(RowState state) => state switch
    {
        RowState.Active => "AccentTextBrush",
        RowState.Done => "SuccessTextBrush",
        RowState.Failed => "DangerTextBrush",
        RowState.Disabled => "TextDisabledBrush",
        _ => "TextPrimaryBrush",
    };

    /// <summary>按状态列前景令牌取画刷。</summary>
    /// <param name="state">行状态。</param>
    public static Brush StatusForeground(RowState state) => Resolve(StatusForegroundKey(state));

    private static Brush Resolve(string key)
    {
        if (Application.Current?.Resources is ResourceDictionary resources
            && resources.TryGetValue(key, out object? found)
            && found is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>按令牌取一个 <c>double</c>（缺失时回落到调用方给的默认值）。</summary>
    /// <param name="key">令牌键。</param>
    /// <param name="fallback">资源缺失时的兜底值。</param>
    internal static double Double(string key, double fallback)
    {
        if (Application.Current?.Resources is ResourceDictionary resources
            && resources.TryGetValue(key, out object? found)
            && found is double value)
        {
            return value;
        }

        return fallback;
    }

    /// <summary>把绑定值归一成 <see cref="RowState"/>（非该类型时按 <see cref="RowState.Normal"/>）。</summary>
    /// <param name="value">绑定值。</param>
    internal static RowState AsState(object value) => value is RowState state ? state : RowState.Normal;
}
