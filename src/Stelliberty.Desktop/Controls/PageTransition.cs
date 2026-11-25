using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;

namespace Stelliberty.Desktop.Controls;

// 旧页较快淡出，缩短两个工具栏同时可见的时间。
internal static class PageTransition
{
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(280);
    public static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(120);
    private static readonly Easing EnterEasing = new SplineEasing
    {
        X1 = 0.16,
        Y1 = 1,
        X2 = 0.3,
        Y2 = 1,
    };
    private static readonly Easing LeaveEasing = new SplineEasing
    {
        X1 = 0.4,
        Y1 = 0,
        X2 = 1,
        Y2 = 1,
    };

    public static Transitions CreateEnterTransitions() => new()
    {
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = EnterDuration, Easing = EnterEasing },
    };

    public static Transitions CreateLeaveTransitions() => new()
    {
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = LeaveDuration, Easing = LeaveEasing },
    };
}
