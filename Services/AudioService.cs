using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BetterQuickSettings.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BetterQuickSettings.Services;

public sealed class AudioService : IDisposable, IMMNotificationClient
{
    private readonly Dispatcher dispatcher;
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly DispatcherTimer refreshTimer;
    private MMDevice? endpoint;
    private bool disposed;
    private bool initialized;
    private IReadOnlyList<AudioOutput> outputs = Array.Empty<AudioOutput>();

    public IReadOnlyList<AudioOutput> Outputs => outputs;
    public float Volume => endpoint?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0;
    public bool Muted => endpoint?.AudioEndpointVolume.Mute ?? false;
    public bool Available => endpoint is not null;
    public string? DefaultOutputId => endpoint?.ID;
    public event Action? Changed;
    public event Action<IReadOnlyList<AudioOutput>, IReadOnlyList<AudioOutput>>? DevicesChanged;

    public AudioService(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(220), DispatcherPriority.Background,
            (_, _) => { refreshTimer!.Stop(); Refresh(); }, dispatcher);
        refreshTimer.Stop();
        enumerator.RegisterEndpointNotificationCallback(this);
        Refresh();
    }

    public void Refresh()
    {
        if (disposed) return;
        try
        {
            var previous = outputs;
            if (endpoint is not null)
            {
                endpoint.AudioEndpointVolume.OnVolumeNotification -= VolumeChanged;
                endpoint.Dispose();
                endpoint = null;
            }
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                endpoint.AudioEndpointVolume.OnVolumeNotification += VolumeChanged;
            }
            var list = new List<AudioOutput>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var factor = device.Properties.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor)
                        ? Convert.ToInt32(device.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value) : -1;
                    // ENDPOINT_FORM_FACTOR: Headphones=3, Headset=5. Independent of display language.
                    list.Add(new(device.ID, device.FriendlyName, factor is 3 or 5, device.ID == endpoint?.ID));
                }
            }
            outputs = list;
            Changed?.Invoke();
            if (initialized) DevicesChanged?.Invoke(previous, outputs);
            initialized = true;
        }
        catch (COMException) { Changed?.Invoke(); }
    }

    public void SetVolume(double percent)
    {
        if (endpoint is null) return;
        endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(percent / 100, 0, 1);
    }

    public void ToggleMute()
    {
        if (endpoint is not null) endpoint.AudioEndpointVolume.Mute = !endpoint.AudioEndpointVolume.Mute;
    }

    public void SelectOutput(string id)
    {
        // Windows has no public default-endpoint setter. Keep this isolated, and surface COM failures.
        var client = new PolicyConfigClient();
        try
        {
            var policy = (IPolicyConfig)client;
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
        }
        finally { Marshal.ReleaseComObject(client); }
        Refresh();
    }

    private void VolumeChanged(AudioVolumeNotificationData data) => Dispatch(() => Changed?.Invoke());
    private void ScheduleRefresh() => Dispatch(() => { refreshTimer.Stop(); refreshTimer.Start(); });
    private void Dispatch(Action action)
    {
        if (!disposed && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }

    public void OnDeviceStateChanged(string id, DeviceState state) => ScheduleRefresh();
    public void OnDeviceAdded(string id) => ScheduleRefresh();
    public void OnDeviceRemoved(string id) => ScheduleRefresh();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id)
    {
        if (flow == DataFlow.Render) ScheduleRefresh();
    }
    public void OnPropertyValueChanged(string id, PropertyKey key)
    {
        if (key.Equals(PropertyKeys.PKEY_Device_FriendlyName) || key.Equals(PropertyKeys.PKEY_AudioEndpoint_FormFactor))
            ScheduleRefresh();
    }

    public void Dispose()
    {
        disposed = true;
        refreshTimer.Stop();
        enumerator.UnregisterEndpointNotificationCallback(this);
        if (endpoint is not null)
        {
            endpoint.AudioEndpointVolume.OnVolumeNotification -= VolumeChanged;
            endpoint.Dispose();
        }
        enumerator.Dispose();
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    // IPolicyConfig's native vtable. Unused slots must stay in their original order.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        void SetEndpointVisibility();
    }
}
