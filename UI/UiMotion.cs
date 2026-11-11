using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BetterQuickSettings.UI;

/// <summary>Short, event-driven transitions. Hidden controls and reduced-motion mode never animate.</summary>
public static class UiMotion
{
    public static readonly RoutedEvent ValueInteractionStartedEvent = EventManager.RegisterRoutedEvent(
        "ValueInteractionStarted", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(UiMotion));
    public static readonly RoutedEvent ValueCommittedEvent = EventManager.RegisterRoutedEvent(
        "ValueCommitted", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(UiMotion));
    public static readonly RoutedEvent ValueInteractionCancelledEvent = EventManager.RegisterRoutedEvent(
        "ValueInteractionCancelled", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(UiMotion));
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached(
        "Interactive", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, InteractiveChanged));
    public static readonly DependencyProperty FluidProperty = DependencyProperty.RegisterAttached(
        "Fluid", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, FluidChanged));
    public static readonly DependencyProperty SuppressProperty = DependencyProperty.RegisterAttached(
        "Suppress", typeof(bool), typeof(UiMotion), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetInteractive(DependencyObject value) => (bool)value.GetValue(InteractiveProperty);
    public static void SetInteractive(DependencyObject value, bool enabled) => value.SetValue(InteractiveProperty, enabled);
    public static bool GetFluid(DependencyObject value) => (bool)value.GetValue(FluidProperty);
    public static void SetFluid(DependencyObject value, bool enabled) => value.SetValue(FluidProperty, enabled);
    public static bool GetSuppress(DependencyObject value) => (bool)value.GetValue(SuppressProperty);
    public static void SetSuppress(DependencyObject value, bool enabled) => value.SetValue(SuppressProperty, enabled);

    private sealed class State
    {
        public bool Attached, Dragging, KeyboardEditing;
        public ScaleTransform? Scale;
        public FrameworkElement? Hover;
        public ProgressBar? Progress;
        public long ProgressUntil;
        public bool Selected;
        public long ToggleUntil;
        public SolidColorBrush? ToggleBrush;
        public TranslateTransform? ToggleTranslate;
    }
    private static readonly ConditionalWeakTable<Control, State> States = new();
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, int>> Versions = new();
    private static readonly DependencyPropertyDescriptor Pressed = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));
    public static bool Enabled => SystemParameters.ClientAreaAnimation;
    private static bool Animate(UIElement element) => Enabled && element.IsVisible && !GetSuppress(element);

    public static void Tween(IAnimatable target, DependencyProperty property, double value, int milliseconds,
        bool animate = true, double? from = null, double? finalBase = null)
    {
        var dependency = (DependencyObject)target;
        var start = from ?? (double)dependency.GetValue(property);
        var versions = Versions.GetOrCreateValue(dependency);
        var version = versions.TryGetValue(property, out var previous) ? previous + 1 : 1;
        versions[property] = version;
        // Update the real base value under the old clock before replacing it. SetCurrentValue
        // would revert to the previous local/template value when an animation stops.
        dependency.SetValue(property, start);
        target.BeginAnimation(property, null);
        if (!animate || !Enabled || Math.Abs(start - value) < 0.0001)
        { dependency.SetValue(property, finalBase ?? value); return; }
        var animation = new DoubleAnimation(start, value, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (versions[property] != version) return;
            dependency.SetValue(property, finalBase ?? value);
            target.BeginAnimation(property, null);
        };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static void Reveal(FrameworkElement element, int milliseconds = 140, double distance = 5)
    {
        if (!Animate(element)) { element.Opacity = 1; return; }
        var offset = new TranslateTransform();
        element.RenderTransform = offset;
        Tween(offset, TranslateTransform.YProperty, 0, milliseconds, from: distance);
        Tween(element, UIElement.OpacityProperty, 1, milliseconds, from: 0.65);
    }


    private static void InteractiveChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Control control) return;
        if ((bool)args.NewValue)
        {
            control.Loaded += InteractiveLoaded; control.Unloaded += InteractiveUnloaded;
            if (control.IsLoaded) AttachInteractive(control);
        }
        else
        {
            control.Loaded -= InteractiveLoaded; control.Unloaded -= InteractiveUnloaded;
            DetachInteractive(control);
        }
    }
    private static void InteractiveLoaded(object sender, RoutedEventArgs args) => AttachInteractive((Control)sender);
    private static void InteractiveUnloaded(object sender, RoutedEventArgs args) => DetachInteractive((Control)sender);
    private static void AttachInteractive(Control control)
    {
        var state = States.GetOrCreateValue(control);
        if (state.Attached) return;
        state.Attached = true;
        control.ApplyTemplate();
        state.Scale = new ScaleTransform(1, 1);
        control.RenderTransformOrigin = new Point(0.5, 0.5);
        control.RenderTransform = state.Scale;
        state.Hover = control.Template?.FindName("HoverOverlay", control) as FrameworkElement;
        control.MouseEnter += HoverEnter; control.MouseLeave += HoverLeave;
        if (control is ButtonBase button) Pressed.AddValueChanged(button, PressedChanged);
        if (control is ToggleButton toggle)
        {
            toggle.Checked += ToggleChanged; toggle.Unchecked += ToggleChanged; toggle.Indeterminate += ToggleChanged;
            UpdateToggle(toggle, false);
        }
    }
    private static void DetachInteractive(Control control)
    {
        if (!States.TryGetValue(control, out var state) || !state.Attached) return;
        state.Attached = false;
        control.MouseEnter -= HoverEnter; control.MouseLeave -= HoverLeave;
        if (control is ButtonBase button) Pressed.RemoveValueChanged(button, PressedChanged);
        if (control is ToggleButton toggle)
        { toggle.Checked -= ToggleChanged; toggle.Unchecked -= ToggleChanged; toggle.Indeterminate -= ToggleChanged; }
    }
    private static void HoverEnter(object sender, MouseEventArgs args) => Hover((Control)sender, true);
    private static void HoverLeave(object sender, MouseEventArgs args) => Hover((Control)sender, false);
    private static void Hover(Control control, bool inside)
    {
        if (States.TryGetValue(control, out var state) && state.Hover is not null)
            Tween(state.Hover, UIElement.OpacityProperty, inside ? 0.065 : 0, inside ? 110 : 160, Animate(control));
    }
    private static void PressedChanged(object? sender, EventArgs args)
    {
        if (sender is not ButtonBase button || !States.TryGetValue(button, out var state) || state.Scale is null) return;
        var scale = button.IsPressed ? 0.965 : 1;
        Tween(state.Scale, ScaleTransform.ScaleXProperty, scale, button.IsPressed ? 70 : 150, Animate(button));
        Tween(state.Scale, ScaleTransform.ScaleYProperty, scale, button.IsPressed ? 70 : 150, Animate(button));
    }
    private static void ToggleChanged(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(sender, args.OriginalSource)) UpdateToggle((ToggleButton)sender, true);
    }
    private static void UpdateToggle(ToggleButton toggle, bool animate)
    {
        animate &= Animate(toggle);
        var selected = toggle.IsChecked == true;
        var state = States.GetOrCreateValue(toggle);
        var running = Environment.TickCount64 < state.ToggleUntil;
        if (toggle.Template?.FindName("SelectedSurface", toggle) is FrameworkElement surface)
            Tween(surface, UIElement.OpacityProperty, selected ? 1 : 0, 150, animate,
                running ? surface.Opacity : state.Selected ? 1 : 0);
        if (toggle.Template?.FindName("SwitchKnob", toggle) is FrameworkElement knob)
        {
            state.ToggleTranslate ??= new TranslateTransform();
            knob.RenderTransform = state.ToggleTranslate;
            Tween(state.ToggleTranslate, TranslateTransform.XProperty, selected ? 13 : 0, 170, animate,
                running ? state.ToggleTranslate.X : state.Selected ? 13 : 0);
        }
        if (toggle.Template?.FindName("SwitchRail", toggle) is Border rail && rail.Background is SolidColorBrush brush)
        {
            state.ToggleBrush ??= new SolidColorBrush(brush.Color);
            brush = state.ToggleBrush;
            rail.Background = brush;
            var from = brush.Color;
            var to = selected ? Color.FromRgb(103, 153, 217) : Color.FromArgb(56, 255, 255, 255);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            if (animate) brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(from, to, TimeSpan.FromMilliseconds(170)) { FillBehavior = FillBehavior.Stop });
        }
        state.Selected = selected;
        state.ToggleUntil = animate ? Environment.TickCount64 + 170 : 0;
    }

    private static void FluidChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Slider slider) return;
        if ((bool)args.NewValue)
        {
            slider.Loaded += FluidLoaded; slider.Unloaded += FluidUnloaded;
            if (slider.IsLoaded) AttachFluid(slider);
        }
        else { slider.Loaded -= FluidLoaded; slider.Unloaded -= FluidUnloaded; DetachFluid(slider); }
    }
    private static void FluidLoaded(object sender, RoutedEventArgs args) => AttachFluid((Slider)sender);
    private static void FluidUnloaded(object sender, RoutedEventArgs args) => DetachFluid((Slider)sender);
    private static void AttachFluid(Slider slider)
    {
        var state = States.GetOrCreateValue(slider);
        if (state.Attached) return;
        state.Attached = true;
        slider.ApplyTemplate();
        state.Progress = slider.Template?.FindName("FillProgress", slider) as ProgressBar;
        state.Hover = slider.Template?.FindName("HoverOverlay", slider) as FrameworkElement;
        state.Scale = new ScaleTransform(1, 1);
        slider.RenderTransformOrigin = new Point(0.5, 0.5); slider.RenderTransform = state.Scale;
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SliderDown), true);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(SliderUp), true);
        slider.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(SliderMove), true);
        slider.LostMouseCapture += SliderLostCapture;
        slider.PreviewKeyDown += SliderKeyDown;
        slider.PreviewKeyUp += SliderKeyUp;
        slider.LostKeyboardFocus += SliderLostKeyboardFocus;
        slider.ValueChanged += SliderValueChanged;
        slider.MouseEnter += HoverEnter; slider.MouseLeave += HoverLeave;
    }
    private static void DetachFluid(Slider slider)
    {
        if (!States.TryGetValue(slider, out var state) || !state.Attached) return;
        state.Attached = false;
        if (state.Dragging || state.KeyboardEditing) slider.RaiseEvent(new RoutedEventArgs(ValueInteractionCancelledEvent));
        state.Dragging = state.KeyboardEditing = false;
        slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SliderDown));
        slider.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(SliderUp));
        slider.RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(SliderMove));
        slider.LostMouseCapture -= SliderLostCapture;
        slider.PreviewKeyDown -= SliderKeyDown;
        slider.PreviewKeyUp -= SliderKeyUp;
        slider.LostKeyboardFocus -= SliderLostKeyboardFocus;
        slider.ValueChanged -= SliderValueChanged;
        slider.MouseEnter -= HoverEnter; slider.MouseLeave -= HoverLeave;
    }
    private static void SliderDown(object sender, MouseButtonEventArgs args)
    {
        var slider = (Slider)sender;
        if (!slider.IsEnabled) return;
        var state = States.GetOrCreateValue(slider);
        slider.Focus(); state.Dragging = slider.CaptureMouse();
        if (state.Dragging) slider.RaiseEvent(new RoutedEventArgs(ValueInteractionStartedEvent));
        SetSliderAt(slider, args.GetPosition(slider).X);
        if (state.Scale is not null) Tween(state.Scale, ScaleTransform.ScaleYProperty, 1.035, 90, Animate(slider));
        args.Handled = true;
    }
    private static void SliderMove(object sender, MouseEventArgs args)
    {
        var slider = (Slider)sender;
        if (States.TryGetValue(slider, out var state) && state.Dragging && args.LeftButton == MouseButtonState.Pressed)
        { SetSliderAt(slider, args.GetPosition(slider).X); args.Handled = true; }
    }
    private static void SliderUp(object sender, MouseButtonEventArgs args)
    {
        var slider = (Slider)sender;
        if (!States.TryGetValue(slider, out var state) || !state.Dragging) return;
        SetSliderAt(slider, args.GetPosition(slider).X);
        state.Dragging = false; slider.ReleaseMouseCapture();
        if (state.Scale is not null) Tween(state.Scale, ScaleTransform.ScaleYProperty, 1, 150, Animate(slider));
        args.Handled = true;
        slider.RaiseEvent(new RoutedEventArgs(ValueCommittedEvent));
    }
    private static void SliderLostCapture(object sender, MouseEventArgs args)
    {
        var slider = (Slider)sender;
        if (!States.TryGetValue(slider, out var state)) return;
        if (state.Dragging) slider.RaiseEvent(new RoutedEventArgs(ValueInteractionCancelledEvent));
        state.Dragging = false;
        if (state.Scale is not null) Tween(state.Scale, ScaleTransform.ScaleYProperty, 1, 150, Animate(slider));
    }
    private static bool IsSliderKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or
        Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private static void SliderKeyDown(object sender, KeyEventArgs args)
    {
        var slider = (Slider)sender;
        if (!slider.IsEnabled || !IsSliderKey(args.Key) || !States.TryGetValue(slider, out var state) || state.KeyboardEditing) return;
        state.KeyboardEditing = true;
        slider.RaiseEvent(new RoutedEventArgs(ValueInteractionStartedEvent));
    }
    private static void SliderKeyUp(object sender, KeyEventArgs args)
    {
        var slider = (Slider)sender;
        if (!IsSliderKey(args.Key) || !States.TryGetValue(slider, out var state) || !state.KeyboardEditing) return;
        state.KeyboardEditing = false;
        slider.RaiseEvent(new RoutedEventArgs(ValueCommittedEvent));
    }
    private static void SliderLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        var slider = (Slider)sender;
        if (!States.TryGetValue(slider, out var state) || !state.KeyboardEditing) return;
        state.KeyboardEditing = false;
        slider.RaiseEvent(new RoutedEventArgs(ValueInteractionCancelledEvent));
    }
    private static void SetSliderAt(Slider slider, double x)
    {
        if (slider.ActualWidth <= 0) return;
        var value = slider.Minimum + Math.Clamp(x / slider.ActualWidth, 0, 1) * (slider.Maximum - slider.Minimum);
        if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
            value = slider.Minimum + Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
        slider.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(value, slider.Minimum, slider.Maximum));
    }
    private static void SliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        var slider = (Slider)sender;
        // The decorative ProgressBar also raises this routed event. Never feed animation frames back into input.
        if (!ReferenceEquals(args.OriginalSource, slider) || !States.TryGetValue(slider, out var state) || state.Progress is null) return;
        var duration = 100;
        var from = Environment.TickCount64 < state.ProgressUntil ? state.Progress.Value : args.OldValue;
        state.ProgressUntil = Environment.TickCount64 + duration;
        Tween(state.Progress, RangeBase.ValueProperty, args.NewValue, duration, Animate(slider) && !state.Dragging, from);
    }

}
