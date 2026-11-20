using Microsoft.UI.Dispatching;

namespace Pane.Services.Traffic;

internal sealed class ProcessTrafficMonitor
{
    internal const int DesktopRankingLimit = 2;
    readonly ProcessTrafficClient client = new();
    readonly CancellationTokenSource stop = new();
    readonly DispatcherQueueTimer timer;
    Action<ProcessTrafficSample>? listeners;
    Action<ProcessTrafficSample>? detailListener;
    TrafficRankingQuery? detailQuery;
    int detailVersion;
    Task<(ProcessTrafficSample Primary, ProcessTrafficSample? Ranked)>? inFlight;
    bool? requestedEnabled;
    bool retryRequested;
    bool settingsWriteFailed;

    public ProcessTrafficMonitor(DispatcherQueue queue)
    {
        timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        timer.Tick += Tick;
        timer.Start();
        Refresh();
    }

    public void Subscribe(Action<ProcessTrafficSample> listener)
    {
        listeners += listener;
        timer.Interval = TimeSpan.FromSeconds(1);
        Refresh();
    }

    public void Unsubscribe(Action<ProcessTrafficSample> listener)
    {
        listeners -= listener;
        UpdateInterval();
    }

    public void SubscribeDetail(Action<ProcessTrafficSample> listener, TrafficRankingQuery? query)
    {
        detailListener = listener;
        detailQuery = query;
        detailVersion++;
        UpdateInterval();
        Refresh();
    }

    public void UnsubscribeDetail()
    {
        detailListener = null;
        detailQuery = null;
        detailVersion++;
        UpdateInterval();
    }

    public void SetDetailQuery(TrafficRankingQuery? query)
    {
        if (detailListener == null || detailQuery == query) return;
        detailQuery = query;
        detailVersion++;
    }

    void UpdateInterval() => timer.Interval = TimeSpan.FromSeconds(listeners == null && detailListener == null ? 5 : 1);

    public void SetEnabled(bool enabled)
    {
        requestedEnabled = enabled;
        Refresh();
    }

    public void Retry()
    {
        retryRequested = true;
        Refresh();
    }

    void Tick(DispatcherQueueTimer sender, object args) => Refresh();

    async void Refresh()
    {
        if (inFlight != null || stop.IsCancellationRequested) return;
        var token = stop.Token;
        var requested = requestedEnabled;
        requestedEnabled = null;
        var retry = retryRequested;
        retryRequested = false;
        var card = listeners != null;
        var detail = detailListener != null;
        var query = detail ? detailQuery : null;
        var version = detailVersion;
        var snapshotLimit = detail ? ProcessTrafficClient.DetailRankingLimit : card ? DesktopRankingLimit : 0;
        var work = Task.Run(async () =>
        {
            var primary = await client.SampleAsync(requested, snapshotLimit, token, card ? null : query, retry).ConfigureAwait(false);
            var ranked = requested == null && card && query is { } contract && primary.State is ProcessTrafficState.Live or ProcessTrafficState.Partial
                ? await client.ReadRankingAsync(contract, token).ConfigureAwait(false) : null;
            return (Primary: primary, Ranked: ranked);
        }, token);
        inFlight = work;
        try
        {
            var (primary, ranked) = await work;
            if (token.IsCancellationRequested) return;
            if (requested != null) settingsWriteFailed = primary.SettingsWriteFailed;
            if (requestedEnabled != null || retryRequested) return;
            if (card) listeners?.Invoke(primary with { SettingsWriteFailed = settingsWriteFailed });
            if (detail && version == detailVersion)
                detailListener?.Invoke((ranked ?? primary) with { SettingsWriteFailed = settingsWriteFailed });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            inFlight = null;
            if (!token.IsCancellationRequested && (requestedEnabled != null || retryRequested || !card && listeners != null || !detail && detailListener != null)) Refresh();
        }
    }

    public async Task StopAsync()
    {
        timer.Stop();
        timer.Tick -= Tick;
        listeners = null;
        detailListener = null;
        stop.Cancel();
        try
        {
            if (inFlight is { } work) await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            client.Dispose();
            stop.Dispose();
        }
    }
}
