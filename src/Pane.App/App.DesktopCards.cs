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

    void ShowDesktopCards()
    {
        if (closing) return;
        CancelDesktopArrangement();
        SelectDesktopCardScreen();
        GlassPanel.RestoreDesktopCards(DesktopCardPanels());
        Present(SecondaryId.DesktopCards, Strings.DesktopCards_Title,
            () => new DesktopCardsView(Glass, CreateTrafficCardView, note => CreateNoteCardView(note.Id), CreateScreenshotCardView,
                ShowTraffic, () => ShowNotes(), () => ShowScreenshots(), ToggleDesktopCards, BeginDesktopArrangement, SaveDesktopArrangement, CancelDesktopArrangement),
            view => ApplyDesktopCards((DesktopCardsView)view));
    }

    void RefreshDesktopCardsOverview()
    {
        if (!closing && secondaries.TryGetValue(SecondaryId.DesktopCards, out var overview) && overview.Shown)
            ApplyDesktopCards((DesktopCardsView)overview.View);
    }

    void ApplyDesktopCards(DesktopCardsView overview)
    {
        var panels = DesktopCardPanels().ToArray();
        if (desktopArrangement is { } editing && (editing.Count != panels.Length || panels.Any(panel => !editing.ContainsKey(panel)) ||
            noteArrangement!.Count != desktopNotes.Count || desktopNotes.Keys.Any(id => !noteArrangement.ContainsKey(id))))
            EndDesktopArrangement(restore: true);
        var hasCards = panels.Length != 0 || desktopNotes.Count != 0;
        overview.ApplyDesktopVisibility(hasCards, desktopCardsHidden);
        overview.ApplyArrangement(hasCards && !desktopCardsHidden, desktopArrangement != null);
        var settings = Store.Current;
        var pinnedNotes = desktopNotesEnabled && settings.DesktopNotes != null
            ? settings.DesktopNotes.Keys.Select(id => notes?.Find(id)).OfType<NoteDocument>()
            : [];
        var pinnedScreenshots = desktopScreenshotsEnabled && settings.DesktopScreenshots != null
            ? settings.DesktopScreenshots.Keys.Take(8).Where(id => id != Guid.Empty)
            : [];
        overview.Apply(desktopTrafficEnabled, desktopNotesEnabled, desktopScreenshotsEnabled,
            desktopTrafficEnabled && settings.DesktopTraffic != null, pinnedNotes, pinnedScreenshots);
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
        var open = DesktopCardPanels().Where(panel => panel.Shown).ToDictionary(panel => panel, panel => panel.DesktopAnchor);
        if (open.Count == 0 && desktopNotes.Count == 0) return false;
        desktopArrangement = open;
        noteArrangement = desktopNotes.ToDictionary(pair => pair.Key, pair => pair.Value.Bounds);
        foreach (var panel in open.Keys) panel.BeginDesktopArrangement();
        if (secondaries.TryGetValue(SecondaryId.DesktopCards, out var overview)) overview.PlacePanelInZOrder(0);
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
    }

    void QueueDesktopReflow()
    {
        if (closing || desktopReflowQueued) return;
        desktopReflowQueued = queue.TryEnqueue(() =>
        {
            desktopReflowQueued = false;
            if (closing) return;
            if (desktopArrangement != null) CancelDesktopArrangement();
            else GlassPanel.RestoreDesktopCards(DesktopCardPanels());
            foreach (var (id, note) in desktopNotes)
                note.Reposition(Store.Current.DesktopNotes?.GetValueOrDefault(id), desktopScreenPoint ?? default, note.Size, DesktopCardObstacles(exceptNote: note));
            RefreshDesktopCardsOverview();
        });
    }

    GlassPanel? CreateDesktopCard(string title, UserControl content, IPanelView view, LauncherPosition? position,
        Action<LauncherPosition?> save)
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
            panel = new GlassPanel(title, content, view, Glass, desktopCard: true)
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
