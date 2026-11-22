using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Screenshots;
using Pane.Settings;
using Pane.Views;

namespace Pane;

public sealed partial class App
{
    ScreenshotStore? screenshotStore;
    NativeScreenshotCapture? screenshotCapture;
    CancellationTokenSource? hotkeyCapture;
    NativeScreenshotOcr? screenshotOcr;
    OcrHistoryStore? ocrHistory;
    readonly Dictionary<Guid, GlassPanel> desktopScreenshots = [];
    bool desktopScreenshotsEnabled;

    ScreenshotStore GetScreenshots() => screenshotStore ??= new(Path.Combine(Store.DirectoryPath, "screenshots"), Store.StorageSync);
    NativeScreenshotCapture GetScreenshotCapture() => screenshotCapture ??= new(GetScreenshots(),
        Path.Combine(Store.DirectoryPath, "capture-sessions"), TryPinScreenshot, RollBackScreenshotPin, Store.ScreenshotPinSync);
    NativeScreenshotOcr GetScreenshotOcr() => screenshotOcr ??= new(GetScreenshots(), WindowsScreenshotOcr.RecognizeAsync);

    void ShowScreenshots(Guid? id = null)
    {
        HideCardDesktop();
        Present(SecondaryId.Screenshots, Strings.Module_Screenshots_Name,
        () => new ScreenshotsView(GetScreenshots(), GetScreenshotCapture(), GetScreenshotOcr(),
            ocrHistory ??= new(Path.Combine(Store.DirectoryPath, OcrHistoryStore.DirectoryName), Store.StorageSync), IsScreenshotPinned,
            ToggleScreenshotPin, ScreenshotEdited, () => Store.Current.LimitScreenshotHistory,
            enabled => Store.Update(settings => settings with { LimitScreenshotHistory = enabled }), ShowTranslate, SaveToolResultAsNote), view =>
        {
            if (id is { } imageId) ((ScreenshotsView)view).Open(imageId);
        });
    }

    async Task CaptureFromHotkeyAsync()
    {
        if (!desktopScreenshotsEnabled || !NativeScreenshotCapture.Available || hotkeyCapture != null || screenshotCapture?.IsRunning == true) return;
        if (secondaries.TryGetValue(SecondaryId.Screenshots, out var openHistory))
        {
            await ((ScreenshotsView)openHistory.View).CaptureNewAsync();
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        hotkeyCapture = request;
        try
        {
            if (await GetScreenshotCapture().CaptureAsync(request.Token) is not { } result || request.IsCancellationRequested) return;
            if (result.Recognize) ShowScreenshots(result.Id);
            if (secondaries.TryGetValue(SecondaryId.Screenshots, out var history))
                await ((ScreenshotsView)history.View).ShowCapturedAsync(result.Id, result.Recognize);
            else if (Store.Current.LimitScreenshotHistory && !GetScreenshots().TrimUnpinnedHistory(result.Id, IsScreenshotPinned))
                shell?.NotifyOnce(Strings.Screenshots_ClearHistoryFailed);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) { ReportFailure(Strings.Screenshots_Capture, error); }
        finally { hotkeyCapture = null; }
    }

    bool IsScreenshotPinned(Guid id) => Store.Current.DesktopScreenshots?.ContainsKey(id) == true;

    DesktopScreenshotView CreateScreenshotCardView(Guid id, Action? removeFromWorkspace = null)
    {
        var store = GetScreenshots();
        var view = new DesktopScreenshotView(store.PathFor(id), store.IsEditable(id) && NativeScreenshotCapture.Available);
        view.OpenRequested += (_, _) => ShowScreenshots(id);
        view.EditRequested += async (_, _) => await EditPinnedScreenshotAsync(id, inWorkspace: removeFromWorkspace != null);
        view.UnpinRequested += (_, _) =>
        {
            if (removeFromWorkspace != null) removeFromWorkspace();
            else UnpinScreenshot(id);
        };
        return view;
    }

    async Task EditPinnedScreenshotAsync(Guid id, bool inWorkspace = false)
    {
        if (closing || !desktopScreenshotsEnabled || !inWorkspace && !IsScreenshotPinned(id) || !GetScreenshots().IsEditable(id)) return;
        ShowScreenshots(id);
        if (secondaries.TryGetValue(SecondaryId.Screenshots, out var history))
            await ((ScreenshotsView)history.View).EditSelectedAsync(id);
    }

    void ScreenshotEdited(Guid id)
    {
        if (closing) return;
        var path = GetScreenshots().PathFor(id);
        if (desktopScreenshots.TryGetValue(id, out var card)) ((DesktopScreenshotView)card.View).Apply(path);
        cardDesktopView?.UpdateScreenshot(id, path);
    }

    void ToggleScreenshotPin(Guid id)
    {
        var pinned = IsScreenshotPinned(id);
        if (pinned) { UnpinScreenshot(id); return; }
        ChangeScreenshotLayout(() => { if (TryPinScreenshot(id)) return; RollBackScreenshotPin(id); });
    }

    bool TryPinScreenshot(Guid id)
    {
        if (closing || !desktopScreenshotsEnabled || !GetScreenshots().Contains(id)) return false;
        if ((Store.Current.DesktopScreenshots?.Count ?? 0) >= ScreenshotStore.DesktopPinLimit)
        {
            shell?.NotifyOnce(Strings.Screenshots_PinLimit);
            return false;
        }
        SaveScreenshotLayout(id, true, null);
        RefreshDesktopScreenshots();
        return desktopScreenshots.ContainsKey(id);
    }

    bool RollBackScreenshotPin(Guid id)
    {
        if (IsScreenshotPinned(id)) UnpinScreenshot(id);
        return !IsScreenshotPinned(id);
    }

    void UnpinScreenshot(Guid id) => ChangeScreenshotLayout(() => SaveScreenshotLayout(id, false, null));

    void ChangeScreenshotLayout(Action change)
    {
        try { change(); RefreshDesktopScreenshots(); }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error)) { ReportFailure(Strings.Screenshots_DesktopCard, error); }
    }

    void SaveScreenshotLayout(Guid id, bool pinned, LauncherPosition? position) => Store.Update(settings =>
    {
        var layout = new Dictionary<Guid, LauncherPosition?>(settings.DesktopScreenshots ?? []);
        if (pinned) layout[id] = position;
        else layout.Remove(id);
        return settings with { DesktopScreenshots = layout.Count == 0 ? null : layout };
    });

    void RefreshDesktopScreenshots()
    {
        if (closing || !desktopScreenshotsEnabled) return;
        var layout = Store.Current.DesktopScreenshots;
        foreach (var id in desktopScreenshots.Keys.ToArray())
            if (layout?.ContainsKey(id) != true && desktopScreenshots.Remove(id, out var removed)) removed.Dispose();
        if (layout != null)
            foreach (var (id, position) in layout.Take(8))
            {
                if (!GetScreenshots().Contains(id) || desktopScreenshots.ContainsKey(id)) continue;
                var view = CreateScreenshotCardView(id);
                if (CreateDesktopCard(Strings.Screenshots_DesktopCard, view, view, position,
                    anchor => SaveScreenshotLayout(id, true, anchor)) is { } panel)
                    desktopScreenshots.Add(id, panel);
            }
        if (secondaries.TryGetValue(SecondaryId.Screenshots, out var history)) ((ScreenshotsView)history.View).RefreshPin();
        RefreshDesktopCardsOverview();
    }

    void ClearDesktopScreenshots()
    {
        desktopScreenshotsEnabled = false;
        foreach (var card in desktopScreenshots.Values) card.Dispose();
        desktopScreenshots.Clear();
        RefreshDesktopCardsOverview();
    }

    sealed class ScreenshotsModule(App app) : Capability(app, ModuleIds.Screenshots, ModuleCategory.System, "scissors")
    {
        public override Task StartAsync(CancellationToken token)
        {
            App.desktopScreenshotsEnabled = true;
            App.RefreshDesktopScreenshots();
            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken token)
        {
            App.hotkeyCapture?.Cancel();
            App.RemoveSecondary(SecondaryId.Screenshots);
            App.ClearDesktopScreenshots();
            if (App.screenshotCapture is { } capture) await capture.DrainAsync();
            if (App.screenshotOcr is { } ocr) await ocr.DrainAsync();
        }
    }
}
