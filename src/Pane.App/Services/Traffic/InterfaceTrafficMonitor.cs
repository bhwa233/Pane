using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Pane.Services.Traffic;

internal sealed class InterfaceTrafficMonitor(DispatcherQueue queue, bool paused)
{
    Action<InterfaceTrafficReading>? listeners;
    DispatcherQueueTimer? timer;
    InterfaceTrafficSampler? sampler;
    InterfaceTrafficReading reading = new(null, paused ? InterfaceTrafficState.Paused : InterfaceTrafficState.Sampling);
    bool sampling;

    public InterfaceTrafficTrend Trend { get; } = new();
    public bool Paused { get; private set; } = paused;
    public InterfaceTrafficReading Current => reading;

    public void Subscribe(Action<InterfaceTrafficReading> listener)
    {
        listeners += listener;
        listener(reading);
        StartSampling();
    }

    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        Paused = paused;
        StopSampling();
        listeners?.Invoke(reading);
        StartSampling();
    }

    void StartSampling()
    {
        if (Paused || timer != null || listeners == null) return;
        sampler = new();
        timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += Refresh;
        timer.Start();
        Refresh(timer, null);
    }

    public void Unsubscribe(Action<InterfaceTrafficReading> listener)
    {
        listeners -= listener;
        if (listeners == null) StopSampling();
    }

    void StopSampling()
    {
        if (timer != null)
        {
            timer.Stop();
            timer.Tick -= Refresh;
            timer = null;
        }
        sampler = null;
        reading = new(null, Paused ? InterfaceTrafficState.Paused : InterfaceTrafficState.Sampling);
        Trend.Clear();
    }

    async void Refresh(DispatcherQueueTimer sender, object? args)
    {
        if (sampling || sampler is not { } active) return;
        sampling = true;
        try
        {
            var next = await Task.Run(active.Sample);
            if (sampler != active) return;
            reading = next;
            Trend.Add(Stopwatch.GetTimestamp(), next.Rate);
            listeners?.Invoke(next);
        }
        finally { sampling = false; }
    }
}
