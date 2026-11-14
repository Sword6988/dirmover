using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DirMover.App.ViewModels;

namespace DirMover.Wpf.Controls;

/// <summary>
/// <see cref="RowState"/> → 背景画刷（WPF 版）。
/// </summary>
/// <remarks>
/// 与 WinUI 版同一条红线：转换器内部<b>只做查表</b>，不出现任何颜色字面量；
/// 颜色只在 Theme/Tokens.xaml 定义一次。
/// 资源缺失时返回透明画刷而不是抛异常——转换器抛异常会被绑定层吞成"整行不渲染"。
/// </remarks>
public sealed class RowStateToBackgroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        TokenBrush.Background(TokenBrush.AsState(value));

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary><see cref="RowState"/> → 前景画刷（整行正文）。</summary>
public sealed class RowStateToForegroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        TokenBrush.Foreground(TokenBrush.AsState(value));

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary><see cref="RowState"/> → 状态列专用前景画刷（处理中青 / 完成绿 / 失败红）。</summary>
public sealed class RowStateToStatusForegroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        TokenBrush.StatusForeground(TokenBrush.AsState(value));

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary><see cref="RowState"/> → 不透明度（只压未勾选的 Disabled 行）。</summary>
public sealed class RowStateToOpacityConverter : IValueConverter
{
    /// <summary>令牌键：未勾选行的整体不透明度。</summary>
    public const string DisabledOpacityKey = "DirMoverDisabledOpacity";

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        TokenBrush.AsState(value) == RowState.Disabled ? TokenBrush.Double(DisabledOpacityKey, 0.55) : 1.0;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary><c>bool</c> → <see cref="Visibility"/>（true 可见）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && flag ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility == Visibility.Visible;
}

/// <summary><c>bool</c> → <see cref="Visibility"/>（true 隐藏）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility != Visibility.Visible;
}

/// <summary><c>bool</c> 取反（提权提示条：未提权才显示）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not bool flag || !flag;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not bool flag || !flag;
}

/// <summary>行状态 → 令牌名 → 画刷的查表实现（三段分离，便于断言"名字"）。</summary>
internal static class TokenBrush
{
    /// <summary>背景令牌名。</summary>
    public static string BackgroundKey(RowState state) => state switch
    {
        RowState.Alt => "RowAltBrush",
        RowState.Pending => "PendingRowBrush",
        RowState.Active => "AccentSubtleBrush",
        RowState.Done => "SuccessSubtleBrush",
        RowState.Failed => "DangerSubtleBrush",
        _ => "SurfaceBrush",
    };

    /// <summary>前景令牌名（只有 Disabled 压灰，失败辨识交给底色与状态文案）。</summary>
    public static string ForegroundKey(RowState state) => state switch
    {
        RowState.Disabled => "TextDisabledBrush",
        _ => "TextPrimaryBrush",
    };

    /// <summary>状态列专用前景令牌名。</summary>
    public static string StatusForegroundKey(RowState state) => state switch
    {
        RowState.Active => "AccentTextBrush",
        RowState.Done => "SuccessTextBrush",
        RowState.Failed => "DangerTextBrush",
        RowState.Disabled => "TextDisabledBrush",
        _ => "TextPrimaryBrush",
    };

    /// <summary>按背景令牌取画刷。</summary>
    public static Brush Background(RowState state) => Resolve(BackgroundKey(state));

    /// <summary>按前景令牌取画刷。</summary>
    public static Brush Foreground(RowState state) => Resolve(ForegroundKey(state));

    /// <summary>按状态列前景令牌取画刷。</summary>
    public static Brush StatusForeground(RowState state) => Resolve(StatusForegroundKey(state));

    private static Brush Resolve(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return Brushes.Transparent;
    }

    /// <summary>按令牌取一个 <c>double</c>（缺失时回落到调用方给的默认值）。</summary>
    internal static double Double(string key, double fallback)
    {
        if (Application.Current?.TryFindResource(key) is double value)
        {
            return value;
        }

        return fallback;
    }

    /// <summary>把绑定值归一成 <see cref="RowState"/>。</summary>
    internal static RowState AsState(object value) => value is RowState state ? state : RowState.Normal;
}
