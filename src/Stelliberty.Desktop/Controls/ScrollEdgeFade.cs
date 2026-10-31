using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Stelliberty.Desktop.Controls;

// 滚动区顶部渐隐：上方仍有被遮住的内容时淡出上边缘，滚到顶端即恢复清晰。
// 底部不参与，避免与列表末项的留白叠加。
public static class ScrollEdgeFade
{
    // 顶部渐隐高度，各页面统一。
    private const double FadeHeight = 18;
    // 每个滚动区复用自己的渐变笔刷，滚动时只改停止点，不反复分配。
    private static readonly ConditionalWeakTable<ScrollViewer, LinearGradientBrush> Masks = new();

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(ScrollEdgeFade));

    public static void SetIsEnabled(ScrollViewer scrollViewer, bool value) => scrollViewer.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(ScrollViewer scrollViewer) => scrollViewer.GetValue(IsEnabledProperty);

    // 供调用方在布局变化或程序化滚动后手动刷新（不改变启用状态）。
    public static void Refresh(ScrollViewer scrollViewer) => UpdateFade(scrollViewer);

    static ScrollEdgeFade()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnIsEnabledChanged);
    }

    private static void OnIsEnabledChanged(ScrollViewer scrollViewer, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is true)
        {
            scrollViewer.ScrollChanged += OnScrollChanged;
            UpdateFade(scrollViewer);
        }
        else
        {
            scrollViewer.ScrollChanged -= OnScrollChanged;
            scrollViewer.OpacityMask = null;
        }
    }

    private static void OnScrollChanged(object? sender, ScrollChangedEventArgs args)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateFade(scrollViewer);
        }
    }

    private static void UpdateFade(ScrollViewer scrollViewer)
    {
        var viewportHeight = scrollViewer.Viewport.Height;
        if (viewportHeight <= 0 || scrollViewer.Offset.Y <= 0)
        {
            scrollViewer.OpacityMask = null;
            return;
        }

        // 渐变长度按视口换算并封顶一半，短视口下不会整屏发虚。
        var fadeOffset = Math.Min(0.5, FadeHeight / viewportHeight);
        var mask = Masks.GetValue(scrollViewer, _ => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, 0),
                new GradientStop(Colors.Black, 1)
            }
        });
        mask.GradientStops[1].Offset = fadeOffset;
        scrollViewer.OpacityMask = mask;
    }
}
