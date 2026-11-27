using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private Border? highlightedCard;
    private RadialGradientBrush? pointerGlow;

    private void DashboardCardEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border card || TryFindResource("HoverEffects") is false || !UiMotion.CanAnimate(card))
            return;

        StopDashboardEffects();
        highlightedCard = card;
        var accent = (Color)FindResource("AccentColor");
        var surface = (Color)FindResource("HeroEnd");
        var glow = Color.FromArgb(surface.A, (byte)((surface.R * 4 + accent.R) / 5),
            (byte)((surface.G * 4 + accent.G) / 5), (byte)((surface.B * 4 + accent.B) / 5));
        pointerGlow = new RadialGradientBrush
        {
            RadiusX = .75,
            RadiusY = .95,
            GradientStops = new GradientStopCollection { new(glow, 0), new(surface, 1) }
        };
        card.Background = pointerGlow;
        DashboardCardMove(card, e);
    }

    private void DashboardCardMove(object sender, MouseEventArgs e)
    {
        if (pointerGlow is null || highlightedCard is not { ActualWidth: > 0, ActualHeight: > 0 } card || !ReferenceEquals(sender, card))
            return;

        Point point = e.GetPosition(card);
        var center = new Point(Math.Clamp(point.X / card.ActualWidth, 0, 1), Math.Clamp(point.Y / card.ActualHeight, 0, 1));
        if ((center - pointerGlow.Center).LengthSquared < .00009)
            return;
        pointerGlow.Center = pointerGlow.GradientOrigin = center;
    }

    private void DashboardCardLeave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, highlightedCard))
            StopDashboardEffects();
    }

    private void DashboardCardVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && ReferenceEquals(sender, highlightedCard))
            StopDashboardEffects();
    }

    private void StopDashboardEffects()
    {
        highlightedCard?.ClearValue(Border.BackgroundProperty);
        highlightedCard = null;
        pointerGlow = null;
        StopBoardFan();
        boardViewport?.StopEffects();
    }

    private void BoardGraphicsEnter(object sender, MouseEventArgs e)
    {
        if (preferences is { ShowHardwareArtwork: true, HoverEffects: true } && UiMotion.CanAnimate(BoardGraphics))
            GraphicsFanRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void BoardGraphicsLeave(object sender, MouseEventArgs e) => StopBoardFan();

    private void BoardGraphicsVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
            StopBoardFan();
    }

    private void StopBoardFan() => GraphicsFanRotation?.BeginAnimation(RotateTransform.AngleProperty, null);

    private void MaintenanceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!webReady && IsLoaded && ReferenceEquals(sender, e.Source) && sender is TabControl { SelectedContent: FrameworkElement panel })
            UiMotion.Enter(panel);
    }

    private void CompleteTaskSound()
    {
        if (preferences.TaskSounds)
            CompletionSound.Play();
    }
}
