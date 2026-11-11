using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterQuickSettings.Services;

/// <summary>A cached, single-voice tap. Device work stays off the input thread; idle playback is released.</summary>
public sealed class VolumeFeedbackService : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent changed = new(false);
    private Thread? worker;
    private bool disposed, interacting, stop;
    private string? requestedDevice;
    private int requestedPlay;
    private long warmUntil;
    private int triggeredCount, outputReady;
    internal int TriggeredCount => Volatile.Read(ref triggeredCount);
    internal bool OutputReady => Volatile.Read(ref outputReady) != 0;

    public void Begin(string device) => Request(device, begin: true, play: false);
    public void Commit(string device) => Request(device, begin: false, play: true);
    public void Cancel()
    {
        lock (gate)
        {
            if (disposed) return;
            interacting = false;
            stop = true;
            changed.Set();
        }
    }

    private void Request(string device, bool begin, bool play)
    {
        lock (gate)
        {
            if (disposed) return;
            requestedDevice = device;
            interacting = begin;
            stop = false;
            warmUntil = Environment.TickCount64 + 1200;
            if (play) requestedPlay++;
            if (worker is null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "BetterQuickSettings volume feedback" };
                worker.Start();
            }
            changed.Set();
        }
    }

    private void Run()
    {
        WasapiOut? output = null;
        MMDevice? device = null;
        TapSource? source = null;
        string? activeDevice = null;
        var played = 0;
        void Release()
        {
            Volatile.Write(ref outputReady, 0);
            try { output?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine("Volume feedback release: " + ex.Message); }
            output = null;
            device?.Dispose(); device = null;
            source = null; activeDevice = null;
        }
        try
        {
            while (true)
            {
                bool quitting, close, held;
                string? target;
                long expires;
                lock (gate)
                {
                    quitting = disposed; close = stop; held = interacting;
                    target = requestedDevice; expires = warmUntil;
                    if (close) played = requestedPlay;
                }
                if (quitting) break;
                if (close || (!held && Environment.TickCount64 >= expires)) Release();
                else if (target is not null)
                {
                    try
                    {
                        if (output is null || activeDevice != target)
                        {
                            Release();
                            using var enumerator = new MMDeviceEnumerator();
                            device = enumerator.GetDevice(target);
                            source = new TapSource();
                            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
                            output.PlaybackStopped += (_, args) =>
                            {
                                if (args.Exception is null) return;
                                lock (gate) { if (!disposed) { stop = true; changed.Set(); } }
                            };
                            output.Init(source);
                            output.Play();
                            Volatile.Write(ref outputReady, 1);
                            activeDevice = target;
                        }
                        lock (gate)
                        {
                            // Only the most recent release survives a burst; tones never stack or queue.
                            if (!disposed && !stop && !interacting && requestedDevice == activeDevice && requestedPlay != played)
                            { source!.Trigger(); played = requestedPlay; Interlocked.Increment(ref triggeredCount); }
                            expires = warmUntil; held = interacting;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Volume feedback: " + ex.Message);
                        Release();
                    }
                }
                var wait = output is null || held ? Timeout.Infinite : (int)Math.Clamp(expires - Environment.TickCount64, 0, 1200);
                changed.WaitOne(wait);
            }
        }
        finally { Release(); changed.Dispose(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (worker is null) changed.Dispose(); else changed.Set();
        }
    }

    private sealed class TapSource : IWaveProvider
    {
        private const int Rate = 48000;
        private const int FadeFrames = 96;
        private static readonly float[] Samples = CreateTap();
        private int trigger, observed, position = Samples.Length, fade;
        private float previous, fadeFrom;
        public WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public void Trigger() => Interlocked.Increment(ref trigger);

        public int Read(byte[] buffer, int offset, int count)
        {
            var latest = Volatile.Read(ref trigger);
            if (latest != observed)
            { observed = latest; position = 0; fade = FadeFrames; fadeFrom = previous; }
            var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            for (var index = 0; index + 1 < samples.Length; index += 2)
            {
                var value = position < Samples.Length ? Samples[position++] : 0;
                if (fade > 0)
                {
                    var mix = 1f - (float)fade-- / FadeFrames;
                    value = fadeFrom * (1 - mix) + value * mix;
                }
                samples[index] = samples[index + 1] = previous = value;
            }
            return count;
        }

        private static float[] CreateTap()
        {
            const double duration = 0.095;
            var samples = new float[(int)(Rate * duration)];
            for (var i = 0; i < samples.Length; i++)
            {
                var t = (double)i / Rate;
                var attack = Math.Pow(Math.Sin(Math.Min(1, t / 0.003) * Math.PI / 2), 2);
                var release = Math.Min(1, (duration - t) / 0.014);
                var phase = 2 * Math.PI * (155 * t + 100 * 0.016 * (1 - Math.Exp(-t / 0.016)));
                var body = Math.Sin(phase) * Math.Exp(-t * 40);
                var overtone = 0.26 * Math.Sin(phase * 2.4) * Math.Exp(-t * 74);
                samples[i] = (float)(0.38 * attack * release * (body + overtone));
            }
            return samples;
        }
    }
}
