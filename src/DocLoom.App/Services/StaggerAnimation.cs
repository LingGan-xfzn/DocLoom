using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DocLoom.App.Services;

/// <summary>
/// 交错入场：列表项按顺序轻微上浮 + 淡入，避免整屏内容"啪"地出现。
/// 只在首次填充或文档切换时调用，不干扰后续交互。
/// </summary>
public static class StaggerAnimation
{
    public static void Play(ItemsControl? items, int maxItems = 14, double stepMs = 26, double riseDistance = 10)
    {
        if (items is null || items.Items.Count == 0)
        {
            return;
        }

        items.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var count = Math.Min(items.Items.Count, maxItems);
            var duration = TimeSpan.FromMilliseconds(280);

            for (var i = 0; i < count; i++)
            {
                if (items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                {
                    continue;
                }

                var begin = TimeSpan.FromMilliseconds(i * stepMs);

                container.Opacity = 1;
                container.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(duration),
                    BeginTime = begin,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });

                var translate = new TranslateTransform(0, 0);
                container.RenderTransform = translate;
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
                {
                    From = riseDistance,
                    To = 0,
                    Duration = new Duration(duration),
                    BeginTime = begin,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
            }
        }));
    }

    /// <summary>整体淡入 + 轻微上浮，用于面板或内容区切换。</summary>
    public static void FadeRise(FrameworkElement? element, double from = 0.25, double riseDistance = 8, int delayMs = 0)
    {
        if (element is null)
        {
            return;
        }

        var transform = element.RenderTransform as TranslateTransform;
        if (transform is null || transform.IsFrozen)
        {
            transform = new TranslateTransform(0, 0);
            element.RenderTransform = transform;
        }

        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = from,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });

        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = riseDistance,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }
}