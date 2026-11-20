using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Traffic;
using Pane.Settings;
using Pane.Views;

namespace Pane;

public sealed partial class App
{
    InterfaceTrafficMonitor? interfaceTraffic;
    ProcessTrafficMonitor? processTraffic;
    Task processTrafficStopped = Task.CompletedTask;
    GlassPanel? desktopTrafficCard;
    bool desktopTrafficEnabled;

    InterfaceTrafficMonitor GetInterfaceTraffic() => interfaceTraffic ??= new(queue, Store.Current.PauseInterfaceTraffic);

    void ShowTraffic() => Present(SecondaryId.Traffic, Strings.Module_Traffic_Name,
        () => new TrafficView(GetInterfaceTraffic(), processTraffic!, SetInterfaceTrafficPaused, ToggleTrafficPin, ShowTrafficHistory, ShowTrafficConnections), view => ((TrafficView)view).RefreshPin(Store.Current.DesktopTraffic != null));

    void SetInterfaceTrafficPaused(bool paused)
    {
        try
        {
            Store.Update(settings => settings.PauseInterfaceTraffic == paused ? settings : settings with { PauseInterfaceTraffic = paused });
            GetInterfaceTraffic().SetPaused(paused);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportFailure(Strings.Traffic_InterfaceMonitoring, error);
        }
    }

    void ShowTrafficHistory(TrafficProcessIdentity? process = null) => Present(SecondaryId.TrafficHistory, Strings.Traffic_History,
        () => new TrafficHistoryView(), view => ((TrafficHistoryView)view).Open(process));

    void ShowTrafficConnections(TrafficProcessIdentity process) => Present(SecondaryId.TrafficConnections, Strings.Traffic_Connections,
        () => new TrafficConnectionsView(), view => ((TrafficConnectionsView)view).Open(process));

    void ToggleTrafficPin() => SetTrafficPin(Store.Current.DesktopTraffic == null);

    DesktopTrafficView CreateTrafficCardView()
    {
        var view = new DesktopTrafficView(GetInterfaceTraffic(), processTraffic!);
        view.OpenRequested += (_, _) => ShowTraffic();
        view.UnpinRequested += (_, _) => SetTrafficPin(false);
        return view;
    }

    void SetTrafficPin(bool pinned)
    {
        try
        {
            Store.Update(settings => settings with { DesktopTraffic = pinned ? new DesktopTrafficSettings() : null });
            RefreshDesktopTraffic();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportFailure(Strings.Traffic_DesktopCard, error);
        }
    }

    void RefreshDesktopTraffic()
    {
        if (closing || !desktopTrafficEnabled) return;
        var layout = Store.Current.DesktopTraffic;
        if (layout == null)
        {
            desktopTrafficCard?.Dispose();
            desktopTrafficCard = null;
        }
        else if (desktopTrafficCard == null)
        {
            var view = CreateTrafficCardView();
            desktopTrafficCard = CreateDesktopCard(Strings.Traffic_DesktopCard, view, view, layout.Position, anchor =>
                Store.Update(settings => settings.DesktopTraffic == null ? settings : settings with { DesktopTraffic = new DesktopTrafficSettings(anchor) }));
        }
        if (secondaries.TryGetValue(SecondaryId.Traffic, out var panel)) ((TrafficView)panel.View).RefreshPin(layout != null);
        RefreshDesktopCardsOverview();
    }

    void ClearDesktopTraffic()
    {
        desktopTrafficEnabled = false;
        desktopTrafficCard?.Dispose();
        desktopTrafficCard = null;
        RefreshDesktopCardsOverview();
    }

    Task StopProcessTrafficAsync()
    {
        if (processTraffic is { } monitor)
        {
            processTraffic = null;
            processTrafficStopped = monitor.StopAsync();
        }
        return processTrafficStopped;
    }

    sealed class TrafficModule(App app) : Capability(app, ModuleIds.Traffic, ModuleCategory.System, "globe")
    {
        public override Task StartAsync(CancellationToken token)
        {
            if (App.closing) return Task.CompletedTask;
            App.processTraffic = new(App.queue);
            App.desktopTrafficEnabled = true;
            App.RefreshDesktopTraffic();
            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken token)
        {
            App.RemoveSecondary(SecondaryId.Traffic);
            App.RemoveSecondary(SecondaryId.TrafficHistory);
            App.RemoveSecondary(SecondaryId.TrafficConnections);
            App.ClearDesktopTraffic();
            await App.StopProcessTrafficAsync();
        }
    }
}
