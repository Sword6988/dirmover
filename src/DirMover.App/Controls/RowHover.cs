using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DirMover.App.Controls;

/// <summary>
/// 行悬停高亮：挂在行模板的<b>底色 Border</b> 上，指针进出时切换模板里"悬停遮罩"层的不透明度。
/// </summary>
/// <remarks>
/// 为什么不用现成的 <c>ListViewItemBackgroundPointerOver</c>：
/// 行模板根节点自带不透明底色（斑马纹 / 状态 tint），ListViewItem 的悬停底色被它整个盖住，
/// 改了主题画刷也看不见。要让悬停可见，只能让模板自己画一层。
/// <para>
/// 为什么挂在底色 Border 而不是行根元素上：WinUI 的 <c>PointerEntered / PointerExited</c>
/// 是<b>直接路由</b>（不冒泡），只有指针真正命中的元素才会收到。行根 Grid 没有底色、
/// 文字块 Background 为 null（不参与命中测试），整个行里唯一稳定可命中的就是底色 Border——
/// 指针落在文字上时命中测试会穿透到它，所以挂在它身上等于覆盖整行（勾选框那一小块除外）。
/// </para>
/// <para>
/// 遮罩层按 <c>x:Name</c> 从父容器里找，<c>IsHitTestVisible=False</c> 保证它不吃指针事件，
/// 滚动与勾选不受影响。
/// </para>
/// </remarks>
public static class RowHover
{
    /// <summary>
    /// 悬停遮罩层的 <c>x:Name</c>。为空表示这一行不启用悬停效果。
    /// </summary>
    public static readonly DependencyProperty OverlayNameProperty = DependencyProperty.RegisterAttached(
        "OverlayName",
        typeof(string),
        typeof(RowHover),
        new PropertyMetadata(null, OnOverlayNameChanged));

    /// <summary>取悬停遮罩层名字。</summary>
    /// <param name="element">行模板的底色 Border。</param>
    /// <returns>遮罩层名字；未设置时为 <c>null</c>。</returns>
    public static string? GetOverlayName(FrameworkElement element)
        => (string?)element.GetValue(OverlayNameProperty);

    /// <summary>设置悬停遮罩层名字。</summary>
    /// <param name="element">行模板的底色 Border。</param>
    /// <param name="value">遮罩层名字。</param>
    public static void SetOverlayName(FrameworkElement element, string? value)
        => element.SetValue(OverlayNameProperty, value);

    private static void OnOverlayNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host)
        {
            return;
        }

        // 模板复用（列表虚拟化）会反复设值，先摘再挂，避免重复订阅。
        host.PointerEntered -= OnPointerEntered;
        host.PointerExited -= OnPointerExited;

        if (e.NewValue is string { Length: > 0 })
        {
            host.PointerEntered += OnPointerEntered;
            host.PointerExited += OnPointerExited;
        }
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e) => SetOverlayOpacity(sender, 1d);

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e) => SetOverlayOpacity(sender, 0d);

    private static void SetOverlayOpacity(object sender, double opacity)
    {
        if (sender is not FrameworkElement host)
        {
            return;
        }

        // 遮罩层与宿主是同级兄弟（都在行模板根 Grid 下），沿可视树向上找父容器。
        // 不按 x:Name 找：DataTemplate 实例化后 Name 属性不保证可用，改按特征识别——
        // 行模板里唯一 IsHitTestVisible=False 的 Border 就是悬停遮罩层。
        if (VisualTreeHelper.GetParent(host) is not Panel parent)
        {
            return;
        }

        foreach (UIElement child in parent.Children)
        {
            if (child is Border { IsHitTestVisible: false })
            {
                child.Opacity = opacity;
                return;
            }
        }
    }
}
