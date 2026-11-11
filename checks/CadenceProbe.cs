using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterQuickSettings.Services;

namespace BetterQuickSettings.Checks;

internal static class CadenceProbe
{
    internal static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var panel = new MainWindow(new LocalStore(Path.Combine(output, "isolated-data"))) { Title = "BetterQuickSettings frame cadence validation" };
        ((CheckBox)panel.FindName("PinCheck")).IsChecked = true;
        panel.Model.ToolsExpanded = false;
        var frames = new List<double>();
        var timer = Stopwatch.StartNew();
        double previous = 0;
        TimeSpan previousRender = TimeSpan.MinValue;
        bool collecting = false;
        EventHandler onFrame = (_, args) =>
        {
            if (!collecting) return;
            var rendering = ((RenderingEventArgs)args).RenderingTime;
            if (rendering == previousRender) return;
            previousRender = rendering;
            var now = timer.Elapsed.TotalMilliseconds;
            if (previous > 0) frames.Add(now - previous);
            previous = now;
        };
        var results = new List<object>();
        async Task Sample(string name, Action action)
        {
            frames.Clear(); previous = 0; previousRender = TimeSpan.MinValue; collecting = true;
            action();
            await Task.Delay(280);
            collecting = false;
            var ordered = frames.Order().ToArray();
            results.Add(new
            {
                Phase = name, Frames = ordered.Length,
                MeanMs = ordered.Length == 0 ? 0 : Math.Round(ordered.Average(), 2),
                P95Ms = ordered.Length == 0 ? 0 : Math.Round(ordered[(int)Math.Floor((ordered.Length - 1) * 0.95)], 2),
                MaxMs = ordered.Length == 0 ? 0 : Math.Round(ordered[^1], 2)
            });
        }
        try
        {
            panel.OpenPanel(); await Task.Delay(500);
            var preferences = (Button)panel.FindName("PreferencesButton");
            var controls = (RadioButton)panel.FindName("ControlsTab");
            var tools = (Expander)panel.FindName("ToolsExpander");
            preferences.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(300);
            controls.IsChecked = true; await Task.Delay(300);
            tools.IsExpanded = true; await Task.Delay(300); tools.IsExpanded = false; await Task.Delay(300);
            CompositionTarget.Rendering += onFrame;
            for (var pass = 0; pass < 3; pass++)
            {
                await Sample("preferences", () => preferences.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                await Sample("controls", () => controls.IsChecked = true);
                await Sample("expand", () => tools.IsExpanded = true);
                await Sample("collapse", () => tools.IsExpanded = false);
            }
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                RenderingTier = RenderCapability.Tier >> 16,
                Phases = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { CompositionTarget.Rendering -= onFrame; panel.Cleanup(); panel.Close(); }
    }
}
