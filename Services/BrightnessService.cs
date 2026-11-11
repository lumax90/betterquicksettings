using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BetterQuickSettings.Interop;

namespace BetterQuickSettings.Services;

public sealed record BrightnessState(bool Available, int Value, string Description);

/// <summary>WMI for built-in panels and DDC/CI for external monitors. Hardware work never runs on the UI thread.</summary>
public sealed class BrightnessService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? instance;
    private IntPtr monitor;
    private bool useWmi;

    public async Task<BrightnessState> ReadAsync(IntPtr monitorHandle)
    {
        await gate.WaitAsync();
        try { return await Task.Run(() => Read(monitorHandle)); }
        finally { gate.Release(); }
    }

    private BrightnessState Read(IntPtr monitorHandle)
    {
        monitor = monitorHandle;
        useWmi = false;
        // Try the actual monitor first. A laptop's unrelated external display must not change its built-in panel.
        var physical = OpenPhysical(monitor);
        try
        {
            foreach (var item in physical)
                if (GetMonitorBrightness(item.Handle, out var min, out var value, out var max) && max > min)
                    return new(true, (int)((value - min) * 100 / (max - min)), "External display · DDC/CI");
        }
        finally { ClosePhysical(physical); }

        // WMI does not expose HMONITOR. Use it only when exactly one logical display is attached.
        if (Native.GetSystemMetrics(80) == 1)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness WHERE Active=True");
                using var results = searcher.Get();
                foreach (ManagementObject result in results)
                {
                    using (result)
                    {
                        instance = (string)result["InstanceName"];
                        useWmi = true;
                        return new(true, Convert.ToInt32(result["CurrentBrightness"]), "Built-in display");
                    }
                }
            }
            catch (ManagementException) { }
            catch (COMException) { }
        }
        return new(false, 0, "Brightness control is unavailable for this display");
    }

    public async Task SetAsync(int percent)
    {
        await gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                percent = Math.Clamp(percent, 0, 100);
                if (useWmi)
                {
                    using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=True");
                    using var results = searcher.Get();
                    foreach (ManagementObject result in results)
                    {
                        using (result)
                            if ((string)result["InstanceName"] == instance)
                            {
                                result.InvokeMethod("WmiSetBrightness", new object[] { 0u, (byte)percent });
                                return;
                            }
                    }
                    throw new InvalidOperationException("The display is unavailable.");
                }
                var physical = OpenPhysical(monitor);
                var succeeded = false;
                try
                {
                    foreach (var item in physical)
                        if (GetMonitorBrightness(item.Handle, out var min, out _, out var max))
                            succeeded |= SetMonitorBrightness(item.Handle, min + (uint)((max - min) * percent / 100));
                }
                finally { ClosePhysical(physical); }
                if (!succeeded) throw new InvalidOperationException("The display did not accept the DDC/CI command.");
            });
        }
        finally { gate.Release(); }
    }

    private static PhysicalMonitor[] OpenPhysical(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(handle, out var count) || count == 0 || count > 32)
            return Array.Empty<PhysicalMonitor>();
        var monitors = new PhysicalMonitor[count];
        return GetPhysicalMonitorsFromHMONITOR(handle, count, monitors) ? monitors : Array.Empty<PhysicalMonitor>();
    }

    private static void ClosePhysical(IEnumerable<PhysicalMonitor> monitors)
    {
        foreach (var item in monitors) DestroyPhysicalMonitor(item.Handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] physical);
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(IntPtr monitor);
}
