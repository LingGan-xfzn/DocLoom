using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DocLoom.App.Services;

/// <summary>
/// 平滑滚动：把滚轮的离散跳格换成一段带缓动的位移。
/// 注意：ScrollViewer.VerticalOffset 的元数据带 IsAnimationProhibited，
/// 不能直接做 DoubleAnimation，这里用 CompositionTarget.Rendering 逐帧插值。
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(SmoothScroll),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty ActiveTweenProperty = DependencyProperty.RegisterAttached(
        "ActiveTween",
        typeof(Tween),
        typeof(SmoothScroll),
        new PropertyMetadata(null));

    private const double PixelsPerNotch = 104;
    private const double DurationMs = 260;

    private sealed class Tween
    {
        public EventHandler? Handler { get; set; }
    }

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
        {
            return;
        }

        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        viewer.Unloaded -= OnUnloaded;

        if (e.NewValue is true)
        {
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            viewer.Unloaded += OnUnloaded;
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer)
        {
            Stop(viewer);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.Delta == 0 || viewer.ScrollableHeight <= 0)
        {
            return;
        }

        e.Handled = true;
        Stop(viewer);

        var notches = e.Delta / 120d;
        var start = viewer.VerticalOffset;
        var target = Math.Clamp(start - notches * PixelsPerNotch, 0, viewer.ScrollableHeight);
        var delta = target - start;
        if (Math.Abs(delta) < 0.5)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var tween = new Tween();

        void Handler(object? _, EventArgs __)
        {
            var progress = Math.Min(1d, stopwatch.Elapsed.TotalMilliseconds / DurationMs);
            var eased = 1 - Math.Pow(1 - progress, 3);
            viewer.ScrollToVerticalOffset(start + delta * eased);

            if (progress >= 1 && tween.Handler is not null)
            {
                CompositionTarget.Rendering -= tween.Handler;
                viewer.ClearValue(ActiveTweenProperty);
                tween.Handler = null;
            }
        }

        tween.Handler = Handler;
        viewer.SetValue(ActiveTweenProperty, tween);
        CompositionTarget.Rendering += Handler;
    }

    private static void Stop(ScrollViewer viewer)
    {
        if (viewer.GetValue(ActiveTweenProperty) is not Tween tween || tween.Handler is null)
        {
            return;
        }

        CompositionTarget.Rendering -= tween.Handler;
        tween.Handler = null;
        viewer.ClearValue(ActiveTweenProperty);
    }
}