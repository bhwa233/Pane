using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Settings;
using Pane.Views;
using Windows.Graphics;

namespace Pane;

public sealed partial class App
{
    Dictionary<GlassPanel, LauncherPosition?>? desktopArrangement;
    Dictionary<Guid, RectInt32>? noteArrangement;
    Native.Point? desktopScreenPoint;
    bool desktopReflowQueued, desktopCardsHidden;
    CardDesktopOverlay? cardDesktopWindow;
    DesktopCardsView? cardDesktopView;
    bool cardDesktopShown;
    ContentControl? cardDesktopTraffic;
    DesktopCardTrigger? desktopTrigger;

    void StartCardDesktopTrigger()
    {
        if (demo) return;
        desktopTrigger = new(point => queue.TryEnqueue(() => ShowCardDesktopAt(point)),
            () => !closing && !cardDesktopShown && Store.Current.ShowCardDesktopTrigger);
        desktopTrigger.Refresh();
    }

    internal void SetCardDesktopTrigger(bool enabled)
    {
        Store.Update(settings => settings with { ShowCardDesktopTrigger = enabled });
        desktopTrigger?.Refresh();
    }

    void CloseCardDesktop()
    {
        ReturnCardDesktopTraffic();
        cardDesktopView?.Dispose();
        cardDesktopWindow?.Dispose();
        desktopTrigger?.Dispose();
    }

    void ShowDesktopCards()
    {
        if (closing) return;
        Native.GetCursorPos(out var point);
        ShowCardDesktopAt(point);
    }

    void ShowCardDesktopAt(Native.Point point)
    {
        if (closing) return;
        foreach (var panel in panels.Values) panel.Hide();
        var display = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
        if (cardDesktopWindow == null)
        {
            var view = new DesktopCardsView(CardDesktopCatalog, CreateCardDesktopContent, SaveCardDesktopPlacement,
                HideCardDesktop, OpenCardTrafficOutside, () => { HideCardDesktop(); OpenSettings(null); });
            cardDesktopView = view;
            cardDesktopWindow = new(view, HideCardDesktop);
        }
        if (Store.Current.CardDesktopLayout == null)
        {
            var layout = new Dictionary<string, CardDesktopPlacement>();
            var settings = Store.Current;
            if (settings.DesktopTraffic is { Hidden: false }) layout["hud"] = new(16, 16, 126, 76);
            foreach (var id in settings.DesktopNotes?.Keys.AsEnumerable() ?? []) Seed($"note:{id:D}");
            foreach (var id in settings.DesktopScreenshots?.Keys.AsEnumerable() ?? []) Seed($"screenshot:{id:D}");
            Store.Update(current => current with { CardDesktopLayout = layout });
            void Seed(string key) => layout[key] = new(16 + 24 * layout.Count, 16 + 24 * layout.Count, 256, 202);
        }
        cardDesktopShown = true;
        cardDesktopView!.RequestedTheme = Glass.Dark ? ElementTheme.Dark : ElementTheme.Light;
        RefreshDesktopCardsOverview();
        cardDesktopWindow.Show(display.WorkArea, activate: !demo);
        cardDesktopView.SetVisible(true);
        cardDesktopView.FocusEntry();
        desktopTrigger?.Refresh();
    }

    void HideCardDesktop()
    {
        if (!cardDesktopShown) return;
        cardDesktopShown = false;
        cardDesktopView!.SetVisible(false);
        cardDesktopWindow!.Hide();
        ReturnCardDesktopTraffic();
        desktopTrigger?.Refresh();
    }

    void RefreshDesktopCardsOverview()
    {
        if (closing || cardDesktopView == null || Store.Current.CardDesktopLayout is not { } layout) return;
        if (cardDesktopTraffic != null && (!layout.ContainsKey("traffic") || !desktopTrafficEnabled))
        {
            ReturnCardDesktopTraffic();
            cardDesktopTraffic = null;
        }
        cardDesktopView.Apply(layout);
        if (cardDesktopShown) BorrowCardDesktopTraffic();
    }

    IEnumerable<(string Key, string Title)> CardDesktopCatalog()
    {
        if (desktopTrafficEnabled)
        {
            yield return ("hud", Strings.Traffic_DesktopCard);
            yield return ("traffic", Strings.Module_Traffic_Name);
        }
        if (desktopNotesEnabled)
            foreach (var note in GetNotes().Documents)
                yield return ($"note:{note.Id:D}", string.IsNullOrWhiteSpace(note.Title) ? Strings.Notes_New : note.Title);
        if (desktopScreenshotsEnabled)
            foreach (var screenshot in GetScreenshots().List())
                yield return ($"screenshot:{screenshot.Id:D}", $"{Strings.Module_Screenshots_Name} · {screenshot.CreatedUtc.ToLocalTime():g}");
    }

    FrameworkElement? CreateCardDesktopContent(string key)
    {
        if (key == "traffic") return cardDesktopTraffic = new ContentControl
        { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        if (key == "hud")
        {
            var hud = new DesktopTrafficView(GetInterfaceTraffic(), processTraffic!);
            hud.OpenRequested += (_, _) => SaveCardDesktopPlacement("traffic",
                Store.Current.CardDesktopLayout?.GetValueOrDefault("traffic") ?? new(48, 48, 608, 500));
            hud.UnpinRequested += (_, _) => SaveCardDesktopPlacement("hud", null);
            return hud;
        }
        var delimiter = key.IndexOf(':');
        if (delimiter < 0 || !Guid.TryParse(key.AsSpan(delimiter + 1), out var id)) return null;
        if (key.StartsWith("note:", StringComparison.Ordinal) && GetNotes().Find(id) != null)
        {
            var note = new DesktopNoteView(GetNoteSessions(), id, new DesktopNoteHost(this, key), inCardDesktop: true);
            note.FoldRequested += (_, _) =>
            {
                if (Store.Current.CardDesktopLayout?.GetValueOrDefault(key) is { } placement && note.PrepareWorkspaceFold())
                    SaveCardDesktopPlacement(key, placement with { Folded = true });
            };
            note.UnpinRequested += (_, _) => SaveCardDesktopPlacement(key, null);
            return note;
        }
        if (!key.StartsWith("screenshot:", StringComparison.Ordinal)) return null;
        return CreateScreenshotCardView(id, () => SaveCardDesktopPlacement(key, null));
    }

    void SaveCardDesktopPlacement(string key, CardDesktopPlacement? placement)
    {
        try
        {
            Store.Update(settings =>
            {
                var layout = new Dictionary<string, CardDesktopPlacement>(settings.CardDesktopLayout ?? []);
                if (placement == null) layout.Remove(key);
                else layout[key] = placement;
                return settings with { CardDesktopLayout = layout };
            });
            RefreshDesktopCardsOverview();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ReportFailure(Strings.DesktopCards_Title, error); }
    }

    IEnumerable<GlassPanel> DesktopCardPanels() => (desktopTrafficCard is { } traffic ? new[] { traffic } : [])
        .Concat(desktopScreenshots.Values);

    System.Drawing.Rectangle[] DesktopCardObstacles(GlassPanel? except = null, DesktopNoteWindow? exceptNote = null) =>
        DesktopCardPanels().Where(card => card != except && card.Shown).Select(card => card.DesktopBounds)
        .Concat(desktopNotes.Values.Where(card => card != exceptNote && card.Shown).Select(card =>
        {
            var bounds = card.Bounds;
            return new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        })).ToArray();

    void SelectDesktopCardScreen()
    {
        if (Native.GetCursorPos(out var point)) desktopScreenPoint = point;
        desktopScreenPoint ??= default(Native.Point);
        foreach (var panel in DesktopCardPanels()) panel.DesktopScreenPoint = desktopScreenPoint;
    }

    void ToggleDesktopCards()
    {
        if (closing) return;
        CancelDesktopArrangement();
        desktopCardsHidden = !desktopCardsHidden;
        var panels = DesktopCardPanels().ToArray();
        if (desktopCardsHidden)
        {
            foreach (var panel in panels) panel.Hide();
            foreach (var note in desktopNotes.Values) note.Hide();
        }
        else
        {
            SelectDesktopCardScreen();
            GlassPanel.RevealDesktopCards(panels, error => ReportFailure(Strings.DesktopCards_Title, error));
            foreach (var note in desktopNotes.Values) note.Show();
        }
        RefreshDesktopCardsOverview();
    }

    bool BeginDesktopArrangement()
    {
        if (closing || desktopCardsHidden || desktopArrangement != null) return false;
        ApplyTrafficEdgeTab(false, allowDock: false);
        var open = DesktopCardPanels().Where(panel => panel.Shown).ToDictionary(panel => panel, panel => panel.DesktopAnchor);
        if (open.Count == 0 && desktopNotes.Count == 0) return false;
        desktopArrangement = open;
        noteArrangement = desktopNotes.ToDictionary(pair => pair.Key, pair => pair.Value.Bounds);
        foreach (var panel in open.Keys) panel.BeginDesktopArrangement();
        return true;
    }

    string? SaveDesktopArrangement()
    {
        if (desktopArrangement is not { } editing) return null;
        foreach (var panel in editing.Keys) panel.FinishDesktopDrag();
        try
        {
            Store.Update(settings =>
            {
                var traffic = settings.DesktopTraffic;
                var notesLayout = settings.DesktopNotes;
                var noteSizes = settings.DesktopNoteSizes;
                foreach (var (id, original) in noteArrangement!)
                {
                    if (!desktopNotes.TryGetValue(id, out var card) || card.Bounds.Equals(original)) continue;
                    if (ReferenceEquals(notesLayout, settings.DesktopNotes)) notesLayout = new(notesLayout ?? []);
                    if (ReferenceEquals(noteSizes, settings.DesktopNoteSizes)) noteSizes = new(noteSizes ?? []);
                    RecordDesktopNoteBounds(id, card, notesLayout!, noteSizes!);
                }
                var screenshotsLayout = UpdatePositions(settings.DesktopScreenshots, desktopScreenshots);
                if (traffic != null && desktopTrafficCard is { } live && Changed(live)) traffic = traffic with { Position = live.DesktopAnchor };
                return traffic == settings.DesktopTraffic && ReferenceEquals(notesLayout, settings.DesktopNotes) &&
                    ReferenceEquals(screenshotsLayout, settings.DesktopScreenshots) ? settings : settings with
                    { DesktopTraffic = traffic, DesktopNotes = notesLayout, DesktopNoteSizes = noteSizes, DesktopScreenshots = screenshotsLayout };
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return App.FailureReason(error) ?? Strings.Settings_SaveFailed;
        }
        EndDesktopArrangement(restore: false);
        return null;

        bool Changed(GlassPanel panel) => editing.TryGetValue(panel, out var baseline) && baseline != panel.DesktopAnchor;

        Dictionary<Guid, LauncherPosition?>? UpdatePositions(Dictionary<Guid, LauncherPosition?>? layout, Dictionary<Guid, GlassPanel> live)
        {
            if (layout == null) return null;
            Dictionary<Guid, LauncherPosition?>? changed = null;
            foreach (var (id, panel) in live)
                if (layout.ContainsKey(id) && Changed(panel))
                    (changed ??= new(layout))[id] = panel.DesktopAnchor;
            return changed ?? layout;
        }
    }

    void CancelDesktopArrangement() => EndDesktopArrangement(restore: true);

    void EndDesktopArrangement(bool restore)
    {
        if (desktopArrangement is not { } editing) return;
        desktopArrangement = null;
        foreach (var (panel, original) in editing) panel.EndDesktopArrangement(original, restore);
        if (restore)
            foreach (var (id, bounds) in noteArrangement!)
                if (desktopNotes.TryGetValue(id, out var card) && !card.Bounds.Equals(bounds)) card.AppWindow.MoveAndResize(bounds);
        noteArrangement = null;
        if (restore) GlassPanel.RestoreDesktopCards(DesktopCardPanels());
        ApplyTrafficHudWindow();
    }

    void QueueDesktopReflow()
    {
        if (closing || desktopReflowQueued) return;
        desktopReflowQueued = queue.TryEnqueue(() =>
        {
            desktopReflowQueued = false;
            if (closing) return;
            desktopTrigger?.Refresh();
            ApplyTrafficEdgeTab(false, allowDock: false);
            if (desktopArrangement != null) CancelDesktopArrangement();
            else GlassPanel.RestoreDesktopCards(DesktopCardPanels());
            foreach (var (id, note) in desktopNotes)
                note.Reposition(Store.Current.DesktopNotes?.GetValueOrDefault(id), desktopScreenPoint ?? default, note.Size, DesktopCardObstacles(exceptNote: note));
            ApplyTrafficHudWindow();
            RefreshDesktopCardsOverview();
        });
    }

    GlassPanel? CreateDesktopCard(string title, UserControl content, IPanelView view, LauncherPosition? position,
        Action<LauncherPosition?> save, double? cornerRadius = null)
    {
        void Remember(LauncherPosition? anchor)
        {
            try { save(anchor); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ReportFailure(title, error); }
        }
        GlassPanel? panel = null;
        try
        {
            if (desktopScreenPoint == null || !desktopCardsHidden && !DesktopCardPanels().Any()) SelectDesktopCardScreen();
            panel = new GlassPanel(title, content, view, Glass, desktopCard: true, cornerRadius: cornerRadius)
            {
                DesktopAnchor = position, EscapeHides = false,
                DesktopScreenPoint = desktopScreenPoint,
                DesktopWorkAreaChanged = QueueDesktopReflow
            };
            var card = panel;
            panel.DesktopObstacles = () => DesktopCardObstacles(card);
            WatchFrames(panel);
            if (desktopCardsHidden) return panel;
            panel.Show(activate: false);
            if (position == null && panel.DesktopPlacement is { } placed)
            {
                panel.DesktopAnchor = placed;
                Remember(placed);
            }
            else if (panel.DesktopPlacement != position) QueueDesktopReflow();
            return panel;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            panel?.Dispose();
            ReportFailure(title, error);
            return null;
        }
    }
}
