using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace BetterQuickSettings.UI;

/// <summary>Compositor blur for an always-on-top flyout, without frame capture or focus-dependent acrylic.</summary>
internal static class GlassBackdrop
{
    private enum WindowAttribute
    {
        UseImmersiveDarkMode = 20,
        WindowCornerPreference = 33,
        BorderColor = 34,
        SystemBackdropType = 38
    }

    private enum BackdropType { None = 1, DesktopAcrylic = 3 }
    private enum CornerPreference { Round = 2 }
    private enum AccentState { Disabled = 0, BlurBehind = 3 }
    private enum Material { Opaque, Blur, AcrylicFallback }
    private sealed class State
    {
        public IntPtr Handle;
        public bool Initialized;
        public bool BorderDisabled;
        public Material Material;
        public bool RequestedBlur;
        public double Amount = double.NaN;
        public double BackgroundLuminance = 0.18;
    }
    private static readonly ConditionalWeakTable<Window, State> States = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public AccentState State; public uint Flags, GradientColor, Animation; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData { public int Attribute; public IntPtr Data; public nuint Size; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte Ac, Flags, Battery, EnergySaver; public uint Remaining, Full; }

    public static void Apply(Window window, bool captureMode, bool force = false, double amount = 55)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null) return;

        var state = States.GetOrCreateValue(window);
        amount = Math.Clamp(double.IsFinite(amount) ? amount : 55, 0, 100);
        var requestedBlur = !captureMode && amount > 0;
        if (state.Initialized && state.Handle == handle && state.RequestedBlur == requestedBlur && !force)
        {
            UpdateMaterials(window, state, amount);
            RestoreSurface(window, source, state.Material);
            return;
        }

        Set(handle, WindowAttribute.UseImmersiveDarkMode, 1);
        Set(handle, WindowAttribute.WindowCornerPreference, (int)CornerPreference.Round);
        state.BorderDisabled = Set(handle, WindowAttribute.BorderColor, unchecked((int)0xFFFFFFFE)) >= 0;

        var mode = Material.Opaque;
        if (requestedBlur && TransparencyAllowed())
        {
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Set(handle, WindowAttribute.SystemBackdropType, (int)BackdropType.None);
            if (DwmExtendFrameIntoClientArea(handle, ref margins) >= 0)
            {
                // The system's Desktop Acrylic becomes solid on deactivation. This small
                // flyout/drop target needs blur while pinned or receiving an external drag.
                if (SetAccent(handle, AccentState.BlurBehind)) mode = Material.Blur;
                else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) &&
                    Set(handle, WindowAttribute.SystemBackdropType, (int)BackdropType.DesktopAcrylic) >= 0)
                    mode = Material.AcrylicFallback;
            }
        }
        if (mode == Material.Opaque)
        {
            SetAccent(handle, AccentState.Disabled);
            Set(handle, WindowAttribute.SystemBackdropType, (int)BackdropType.None);
        }
        state.Handle = handle;
        state.Material = mode;
        state.RequestedBlur = requestedBlur;
        state.Initialized = true;
        state.Amount = double.NaN;
        UpdateMaterials(window, state, amount);
        RestoreSurface(window, source, mode);
    }

    internal static bool NativeBorderDisabled(Window window) => States.TryGetValue(window, out var state) && state.BorderDisabled;
    internal static bool IsComposited(Window window) => States.TryGetValue(window, out var state) && state.Material != Material.Opaque;

    public static void PrepareBackground(Window window)
    {
        if (window.IsVisible) return;
        var state = States.GetOrCreateValue(window);
        var display = BetterQuickSettings.Interop.Native.DisplayAtCursor();
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = window.Width * dpi.DpiScaleX;
        var height = (window.ActualHeight > 0 ? window.ActualHeight : 300) * dpi.DpiScaleY;
        var left = display.WorkingArea.Right - width - 10;
        var top = display.WorkingArea.Bottom - height - 10;
        var dc = GetDC(IntPtr.Zero);
        double light = 0;
        var count = 0;
        try
        {
            // Sample once before opening; the material stays stable while the panel is visible.
            foreach (var x in new[] { 0.25, 0.75 })
                foreach (var y in new[] { 0.25, 0.75 })
                {
                    var color = GetPixel(dc, (int)(left + width * x), (int)(top + height * y));
                    if (color == 0xFFFFFFFF) continue;
                    light += ((color & 255) * 0.2126 + ((color >> 8) & 255) * 0.7152 + ((color >> 16) & 255) * 0.0722) / 255;
                    count++;
                }
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        if (count > 0) state.BackgroundLuminance = light / count;
        state.Amount = double.NaN;
    }

    private static void UpdateMaterials(Window window, State state, double amount)
    {
        if (state.Amount == amount) return;
        var strength = amount / 100;
        var clarity = Math.Pow(strength, 1.25);
        var opacity = amount == 0 ? 1 : 0.93 * Math.Pow(1 - strength, 1.3) + 0.025 * strength;
        var light = amount > 0 && state.BackgroundLuminance * (1 - opacity) + 0.11 * opacity > 0.58;
        static Color Tint(double alpha, byte r, byte g, byte b) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), r, g, b);
        // WPF freezes resources when they are consumed by a template. Publish complete,
        // immutable brushes; never mutate a resource already in use by the visual tree.
        var baseTint = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        baseTint.GradientStops.Add(new GradientStop(Tint(opacity, 31, 36, 44), 0));
        baseTint.GradientStops.Add(new GradientStop(Tint(opacity * 0.85, 21, 26, 33), 0.55));
        baseTint.GradientStops.Add(new GradientStop(Tint(opacity, 32, 37, 46), 1));
        var shine = new RadialGradientBrush
        {
            Center = new Point(0.18, 0), GradientOrigin = new Point(0.18, 0), RadiusX = 0.85, RadiusY = 0.34
        };
        shine.GradientStops.Add(new GradientStop(Tint(0.16 * clarity, 255, 255, 255), 0));
        shine.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        var drawing = new DrawingGroup();
        var geometry = new RectangleGeometry(new Rect(0, 0, 1, 1));
        drawing.Children.Add(new GeometryDrawing(baseTint, null, geometry));
        drawing.Children.Add(new GeometryDrawing(shine, null, geometry));
        var tint = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        var cell = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.2, 1) };
        cell.GradientStops.Add(new GradientStop(light ? Tint(0.18, 255, 255, 255) : Tint(0.24 - clarity * 0.08, 15, 19, 26), 0));
        cell.GradientStops.Add(new GradientStop(light ? Tint(0.10, 255, 255, 255) : Tint(0.30 - clarity * 0.08, 15, 19, 26), 1));
        var header = new SolidColorBrush(light ? Tint(0.32, 255, 255, 255) : Tint(0.38 - clarity * 0.10, 13, 18, 25));
        var ink = new SolidColorBrush(light ? Color.FromRgb(22, 28, 38) : Color.FromRgb(247, 249, 253));
        var muted = new SolidColorBrush(light ? Color.FromRgb(55, 65, 80) : Color.FromRgb(213, 221, 232));
        var accent = new SolidColorBrush(light ? Color.FromRgb(35, 73, 123) : Color.FromRgb(192, 216, 249));
        var fill = new LinearGradientBrush(light ? Color.FromRgb(67, 83, 106) : Color.FromRgb(238, 245, 255),
            light ? Color.FromRgb(36, 51, 74) : Color.FromRgb(188, 207, 234), 90);
        tint.Freeze(); cell.Freeze(); header.Freeze(); ink.Freeze(); muted.Freeze(); accent.Freeze(); fill.Freeze();
        window.Resources["GlassTint"] = tint;
        window.Resources["GlassCell"] = cell;
        window.Resources["GlassHeader"] = header;
        window.Resources["Ink"] = ink;
        window.Resources["Muted"] = muted;
        window.Resources["Accent"] = accent;
        window.Resources["SliderFill"] = fill;
        state.Amount = amount;
    }

    private static void RestoreSurface(Window window, HwndSource source, Material mode)
    {
        var fallback = (SolidColorBrush)window.FindResource("GlassFallback");
        var background = mode == Material.Opaque ? fallback : (Brush)window.FindResource("GlassTint");
        var clear = mode == Material.Opaque ? fallback.Color : Colors.Transparent;
        if (source.CompositionTarget.BackgroundColor != clear)
            source.CompositionTarget.BackgroundColor = clear;
        if (!ReferenceEquals(window.Background, background)) window.Background = background;
    }

    private static bool TransparencyAllowed()
    {
        if (SystemParameters.HighContrast || DwmIsCompositionEnabled(out var composition) < 0 || !composition) return false;
        if (GetSystemPowerStatus(out var power) && power.EnergySaver != 0) return false;
        using var preferences = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return preferences?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
    }

    private static bool SetAccent(IntPtr handle, AccentState state)
    {
        var policy = new AccentPolicy { State = state };
        var size = Marshal.SizeOf<AccentPolicy>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionAttributeData { Attribute = 19, Data = memory, Size = (nuint)size };
            return SetWindowCompositionAttribute(handle, ref data);
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static int Set(IntPtr handle, WindowAttribute attribute, int value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr handle, WindowAttribute attribute, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr handle, ref Margins margins);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr handle, ref CompositionAttributeData data);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
