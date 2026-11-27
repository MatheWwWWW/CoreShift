using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CoreShift.Desktop;

internal static class UiMotion
{
    private static readonly DoubleAnimation Fade = Transition(.89, 1);
    private static readonly DoubleAnimation Slide = Transition(6, 0);

    internal static bool CanAnimate(FrameworkElement element) => SystemParameters.ClientAreaAnimation &&
        element.TryFindResource("AnimateTransitions") is not false &&
        (element.TryFindResource("ReduceEffectsOnBattery") is not true ||
            System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline);

    internal static void Enter(FrameworkElement element)
    {
        if (!CanAnimate(element))
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            if (element.RenderTransform is TranslateTransform shift)
                shift.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }

        if (element.RenderTransform is not TranslateTransform offset || offset.IsFrozen)
        {
            offset = new TranslateTransform();
            element.RenderTransform = offset;
        }
        element.BeginAnimation(UIElement.OpacityProperty, Fade);
        offset.BeginAnimation(TranslateTransform.XProperty, Slide);
    }

    private static DoubleAnimation Transition(double from, double to)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(145))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Freeze();
        return animation;
    }
}
