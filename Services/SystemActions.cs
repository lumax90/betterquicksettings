using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace BetterQuickSettings.Services;

public static class SystemActions
{
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void OpenSettings(string page) => Process.Start(new ProcessStartInfo("ms-settings:" + page) { UseShellExecute = true });

    public static string NetworkLabel()
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet);
            return adapter is null ? "Not connected" : adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi connected" : "Ethernet connected";
        }
        catch (NetworkInformationException) { return "Network settings"; }
    }

    public static async Task<string> BluetoothLabelAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return bluetooth is null ? "Device settings" : bluetooth.State == RadioState.On ? "On" : "Off";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { return "Device settings"; }
    }

    public static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
        return key?.GetValue("BetterQuickSettings") is string || key?.GetValue("Ada") is string;
    }

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable.");
            if (!executable.EndsWith("BetterQuickSettings.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run BetterQuickSettings.exe before enabling startup.");
            key.SetValue("BetterQuickSettings", "\"" + executable + "\" --background");
            key.DeleteValue("Ada", throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue("BetterQuickSettings", throwOnMissingValue: false);
            key.DeleteValue("Ada", throwOnMissingValue: false);
        }
    }

    public static void MigrateLegacyStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
        if (key?.GetValue("Ada") is string && Environment.ProcessPath?.EndsWith("BetterQuickSettings.exe", StringComparison.OrdinalIgnoreCase) == true)
            SetStartWithWindows(true);
    }
}
