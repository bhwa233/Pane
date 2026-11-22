using Microsoft.UI.Dispatching;

namespace Pane.Services.Traffic;

internal sealed class ProcessTrafficMonitor
{
    // The desktop hover panel shows three entries per direction.
    const int DesktopRankingLimit = 3;
    readonly ProcessTrafficClient client = new();
    readonly CancellationTokenSource stop = new();
    readonly DispatcherQueueTimer timer;
    Action<ProcessTrafficSample, ProcessTrafficSample>? listeners;
    Action<ProcessTrafficSample>? detailListener;
    TrafficRankingQuery? detailQuery;
    int detailVersion;
    Task? inFlight;
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

    public void Subscribe(Action<ProcessTrafficSample, ProcessTrafficSample> listener)
    {
        listeners += listener;
        timer.Interval = TimeSpan.FromSeconds(1);
        Refresh();
    }

    public void Unsubscribe(Action<ProcessTrafficSample, ProcessTrafficSample> listener)
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
        var download = new TrafficRankingQuery("", false, TrafficRankingSort.Download, ActiveOnly: true);
        var upload = new TrafficRankingQuery("", false, TrafficRankingSort.Upload, ActiveOnly: true);
        var work = Task.Run(async () =>
        {
            var primary = await client.SampleAsync(requested, snapshotLimit, token, card ? detail ? null : download : query, retry).ConfigureAwait(false);
            var live = primary.State is ProcessTrafficState.Live or ProcessTrafficState.Partial;
            var ranked = requested == null && card && live && query is { } contract
                ? await client.ReadRankingAsync(contract, ProcessTrafficClient.DetailRankingLimit, token).ConfigureAwait(false) : primary;
            var down = card && detail && live
                ? await client.ReadRankingAsync(download, DesktopRankingLimit, token).ConfigureAwait(false) : primary;
            var up = card && live
                ? await client.ReadRankingAsync(upload, DesktopRankingLimit, token).ConfigureAwait(false) : primary;
            return (Primary: ranked, Download: down, Upload: up);
        }, token);
        inFlight = work;
        try
        {
            var (primary, down, up) = await work;
            if (token.IsCancellationRequested) return;
            if (requested != null) settingsWriteFailed = primary.SettingsWriteFailed;
            if (requestedEnabled != null || retryRequested) return;
            if (card) listeners?.Invoke(down, up);
            if (detail && version == detailVersion)
                detailListener?.Invoke(primary with { SettingsWriteFailed = settingsWriteFailed });
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
