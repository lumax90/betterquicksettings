using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterQuickSettings.UI;

/// <summary>A stable flyout shell. Pages scroll inside it; navigation never resizes the native window.</summary>
internal sealed class PanelLayout
{
    public const double PreferredHeight = 360;
    public const double MinimumHeight = 310;
    private readonly Window window;
    private readonly Border surface;
    private readonly Grid content;
    private readonly Action position;
    private readonly Func<double> preferredHeight;
    public bool IsResizing { get; set; }

    public PanelLayout(Window window, Border surface, Grid content, Action position, Func<double> preferredHeight)
    { this.window = window; this.surface = surface; this.content = content; this.position = position; this.preferredHeight = preferredHeight; }

    public void Change(Action action)
    {
        action();
        RefreshSize();
    }

    public void RefreshSize()
    {
        var requested = preferredHeight();
        if (!double.IsFinite(requested)) requested = PreferredHeight;
        var height = Math.Clamp(requested, window.MinHeight, window.MaxHeight);
        window.SizeToContent = SizeToContent.Manual;
        surface.Clip = null;
        surface.RenderTransform = Transform.Identity;
        surface.Height = double.NaN;
        content.Height = double.NaN;
        content.VerticalAlignment = VerticalAlignment.Stretch;
        if (!IsResizing && (double.IsNaN(window.Height) || Math.Abs(window.Height - height) > 0.5)) window.Height = height;
        window.UpdateLayout();
        if (!IsResizing) position();
    }

    public void Finish()
    {
        surface.Clip = null;
        surface.RenderTransform = Transform.Identity;
        content.CacheMode = null;
    }
}
