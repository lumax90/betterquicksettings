using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BetterQuickSettings.UI;

/// <summary>Crossfades two independently scrolling pages inside one fixed viewport.</summary>
internal sealed class PageTransition
{
    private readonly Grid host;
    private int revision;
    public PageTransition(Grid host) => this.host = host;

    public void Show(FrameworkElement? previous, FrameworkElement next, bool animate)
    {
        var version = ++revision;
        var direction = previous is null || host.Children.IndexOf(next) >= host.Children.IndexOf(previous) ? 1 : -1;
        var oldOpacity = previous?.Opacity ?? 1;
        var oldX = (previous?.RenderTransform as TranslateTransform)?.X ?? 0;
        var nextVisible = next.Visibility == Visibility.Visible;
        var nextOpacity = nextVisible ? next.Opacity : 0;
        var nextX = nextVisible ? (next.RenderTransform as TranslateTransform)?.X ?? 0 : direction * 16;
        foreach (FrameworkElement page in host.Children)
        {
            Reset(page);
            page.Visibility = ReferenceEquals(page, next) || (animate && ReferenceEquals(page, previous)) ? Visibility.Visible : Visibility.Collapsed;
            page.IsHitTestVisible = ReferenceEquals(page, next);
            Panel.SetZIndex(page, ReferenceEquals(page, next) ? 1 : 0);
        }
        if (next is ScrollViewer scroll) scroll.ScrollToTop();
        if (!animate || previous is null || !UiMotion.Enabled) { Finish(next); return; }

        var outgoing = new TranslateTransform(oldX, 0);
        previous.RenderTransform = outgoing;
        UiMotion.Tween(outgoing, TranslateTransform.XProperty, -direction * 12, 160, from: oldX);
        UiMotion.Tween(previous, UIElement.OpacityProperty, 0, 150, from: oldOpacity);
        var incoming = new TranslateTransform(nextX, 0);
        next.RenderTransform = incoming;
        UiMotion.Tween(incoming, TranslateTransform.XProperty, 0, 210, from: nextX);
        var fade = new DoubleAnimation(nextOpacity, 1, TimeSpan.FromMilliseconds(210))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
        next.Opacity = nextOpacity;
        fade.Completed += (_, _) => { if (version == revision) Finish(next); };
        next.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    public void Finish(FrameworkElement? selected)
    {
        revision++;
        foreach (FrameworkElement page in host.Children)
        {
            Reset(page);
            page.Visibility = ReferenceEquals(page, selected) ? Visibility.Visible : Visibility.Collapsed;
            page.IsHitTestVisible = ReferenceEquals(page, selected);
        }
    }

    private static void Reset(FrameworkElement page)
    {
        page.BeginAnimation(UIElement.OpacityProperty, null);
        if (page.RenderTransform is TranslateTransform transform)
        { transform.BeginAnimation(TranslateTransform.XProperty, null); transform.BeginAnimation(TranslateTransform.YProperty, null); }
        page.Opacity = 1;
        page.RenderTransform = Transform.Identity;
    }
}
