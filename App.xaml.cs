using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using BetterQuickSettings.Interop;
using BetterQuickSettings.Services;
using BetterQuickSettings.UI;

namespace BetterQuickSettings;

public partial class App : Application
{
    private Mutex? instance;
    private TrayIcon? tray;
    private ShellIntegration? integration;
    private MainWindow? panel;
    private DropHintWindow? dropHint;
    private bool ownsMutex;

    protected override async void OnStartup(StartupEventArgs args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        base.OnStartup(args);
        foreach (var command in new[] { "--drop-receiver", "--external-drag-check", "--drop-source", "--clipboard-check", "--taskbar-drop-check", "--volume-feedback-check" })
        {
            var index = Array.IndexOf(args.Args, command);
            if (index < 0 || index + 1 >= args.Args.Length) continue;
            var directory = Path.GetFullPath(args.Args[index + 1]);
            try
            {
                if (command == "--drop-receiver") await BetterQuickSettings.Checks.ExternalDropProbe.ReceiveAsync(directory);
                else if (command == "--external-drag-check") await BetterQuickSettings.Checks.ExternalDropProbe.RunAsync(directory);
                else if (command == "--drop-source") await BetterQuickSettings.Checks.UtilityProbe.SendFileAsync(directory);
                else if (command == "--clipboard-check") await BetterQuickSettings.Checks.UtilityProbe.ClipboardAsync(directory);
                else if (command == "--taskbar-drop-check") await BetterQuickSettings.Checks.UtilityProbe.TaskbarDropAsync(directory);
                else if (command == "--volume-feedback-check") await BetterQuickSettings.Checks.UtilityProbe.VolumeAsync(directory);
                Shutdown();
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); Shutdown(1);
            }
            return;
        }
        var productIndex = Array.IndexOf(args.Args, "--product-check");
        if (productIndex >= 0 && productIndex + 1 < args.Args.Length)
        {
            var directory = Path.GetFullPath(args.Args[productIndex + 1]);
            try { await BetterQuickSettings.Checks.ProductProbe.RunAsync(directory); Shutdown(); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); Shutdown(1);
            }
            return;
        }
        var cadenceIndex = Array.IndexOf(args.Args, "--cadence-check");
        if (cadenceIndex >= 0 && cadenceIndex + 1 < args.Args.Length)
        {
            var directory = Path.GetFullPath(args.Args[cadenceIndex + 1]);
            try { await BetterQuickSettings.Checks.CadenceProbe.RunAsync(directory); Shutdown(); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); Shutdown(1);
            }
            return;
        }
        var interactionIndex = Array.IndexOf(args.Args, "--interaction-check");
        if (interactionIndex >= 0 && interactionIndex + 1 < args.Args.Length)
        {
            var directory = Path.GetFullPath(args.Args[interactionIndex + 1]);
            try { await BetterQuickSettings.Checks.InteractionProbe.RunAsync(directory); Shutdown(); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }
        var glassIndex = Array.IndexOf(args.Args, "--glass-check");
        if (glassIndex >= 0 && glassIndex + 1 < args.Args.Length)
        {
            var directory = Path.GetFullPath(args.Args[glassIndex + 1]);
            try { await BetterQuickSettings.Checks.GlassProbe.RunAsync(directory); Shutdown(); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }
        var captureIndex = Array.IndexOf(args.Args, "--capture");
        var capturePath = captureIndex >= 0 && captureIndex + 1 < args.Args.Length ? Path.GetFullPath(args.Args[captureIndex + 1]) : null;
        if (capturePath is null)
        {
            // Retain the pre-release identity to avoid running two taskbar replacements during upgrades.
            instance = new Mutex(true, @"Local\Ada.ControlCenter.85a316c7", out ownsMutex);
            if (!ownsMutex)
            {
                var existing = Native.FindWindow(null, BetterQuickSettings.MainWindow.WindowTitle);
                if (existing == IntPtr.Zero) existing = Native.FindWindow(null, BetterQuickSettings.MainWindow.LegacyWindowTitle);
                if (existing != IntPtr.Zero) Native.PostMessage(existing, BetterQuickSettings.MainWindow.ToggleMessage, IntPtr.Zero, IntPtr.Zero);
                Shutdown();
                return;
            }
        }
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            try
            {
                var directory = LocalStore.DefaultDirectoryPath;
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "error.log"), $"{DateTimeOffset.Now:u}\n{eventArgs.Exception}\n");
            }
            catch (IOException) { }
            eventArgs.Handled = true;
            if (panel is not null) panel.Model.Notice = eventArgs.Exception.Message;
        };
        try
        {
            var dataIndex = Array.IndexOf(args.Args, "--data-dir");
            var dataDirectory = dataIndex >= 0 && dataIndex + 1 < args.Args.Length ? Path.GetFullPath(args.Args[dataIndex + 1]) : null;
            var store = capturePath is null ? new LocalStore(dataDirectory) : new LocalStore(Path.Combine(Path.GetDirectoryName(capturePath)!, "preview-data"));
            panel = new MainWindow(store, capturePath is not null);
            MainWindow = panel;
            new WindowInteropHelper(panel).EnsureHandle();
            if (!args.Args.Contains("--background") || capturePath is not null) panel.OpenPanel();

            if (capturePath is null)
            {
                panel.Model.Try(SystemActions.MigrateLegacyStartup);
                SetupTray();
                integration = new ShellIntegration(panel.Model.ReplaceQuickSettings, Dispatcher);
                panel.ShellLayoutChanged += integration.RequestRefresh;
                integration.ToggleRequested += () => Dispatcher.BeginInvoke(new Action(panel.TogglePanel));
                integration.StatusChanged += () => Dispatcher.BeginInvoke(new Action(UpdateIntegrationStatus));
                integration.ScreenshotRequested += () => Dispatcher.BeginInvoke(new Action(panel.Model.ArmScreenshot));
                dropHint = new DropHintWindow();
                dropHint.GlassAmount = panel.Model.GlassAmount;
                new WindowInteropHelper(dropHint).EnsureHandle();
                panel.Model.AppearanceChanged += () => dropHint.GlassAmount = panel.Model.GlassAmount;
                dropHint.FilesDropped += panel.ReceiveFiles;
                integration.DragTargetEntered += (x, y, bounds) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Native.Pressed(1) && !panel.IsFileDragInProgress && !ScreenshotService.IsCaptureToolForeground()) dropHint.OfferAt(x, y, bounds);
                }));
                integration.DragReleased += () => Dispatcher.BeginInvoke(new Action(dropHint.ScheduleHide));
                panel.Model.PreferencesChanged += () => { integration.Enabled = panel.Model.ReplaceQuickSettings; UpdateIntegrationStatus(); };
                panel.Model.FocusCompleted += () => tray?.ShowBalloon("Timer finished", "Your 25-minute session is complete.");
                UpdateIntegrationStatus();
            }
            else panel.Model.IntegrationStatus = "Preview mode";

            await panel.InitializeServicesAsync();
            if (capturePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                await Task.Delay(350);
                panel.Capture(capturePath);
                var controlsWidth = panel.ActualWidth;
                var controlsHeight = panel.ActualHeight;
                panel.ShowNotesForCapture();
                panel.Capture(Path.ChangeExtension(capturePath, "notes.png"));
                panel.ShowPreferencesForCapture();
                panel.Capture(Path.ChangeExtension(capturePath, "preferences.png"));
                File.WriteAllText(Path.ChangeExtension(capturePath, "health.json"), JsonSerializer.Serialize(new
                {
                    panel.Model.AudioAvailable,
                    panel.Model.BrightnessAvailable,
                    MediaAvailable = panel.Model.Media.Available,
                    Outputs = panel.Model.Outputs.Count,
                    panel.Model.Notice,
                    Width = controlsWidth,
                    Height = controlsHeight
                }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown();
            }
        }
        catch (Exception ex)
        {
            if (capturePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                File.WriteAllText(Path.ChangeExtension(capturePath, "error.txt"), ex.ToString());
            }
            else MessageBox.Show(ex.Message, "Could not start BetterQuickSettings", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void UpdateIntegrationStatus()
    {
        if (panel is null || integration is null) return;
        panel.Model.IntegrationStatus = !integration.Enabled ? "Open from the system tray" :
            integration.TaskbarReady && integration.KeyboardReady ? "Taskbar and Win + A connected" :
            integration.KeyboardReady ? "Win + A connected; finding the taskbar" : "Tray available; shortcut unavailable";
    }

    private void SetupTray()
    {
        tray = new TrayIcon(new WindowInteropHelper(panel!).Handle, BetterQuickSettings.MainWindow.WindowTitle);
        tray.OpenRequested += () => panel?.TogglePanel();
        tray.ExitRequested += () => Shutdown();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        integration?.Dispose();
        dropHint?.Close();
        panel?.Cleanup();
        tray?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(args);
    }

}
