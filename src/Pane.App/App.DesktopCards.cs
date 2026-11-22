using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Hosting;
using Pane.Contracts;
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
    bool cardDesktopShown, cardDesktopCardsVisible;
    RectInt32 cardDesktopWorkArea;
    readonly Dictionary<string, Window> cardToolWindows = [];
    ClipboardView? cardClipboard;
    TranslateView? retainedTranslate;
    bool restoreTranslateWindow;
    (nint Window, uint Process) cardPasteTarget;
    record CardTool(string Key, string Title, string Icon, Windows.Foundation.Size Size, Action Attach, Action Detach, Action OpenOutside);

    IEnumerable<CardTool> CardTools()
    {
        if (desktopTrafficEnabled)
            yield return new("traffic", Strings.Module_Traffic_Name, "globe", TrafficHistoryView.PreferredSize,
                BorrowCardDesktopTraffic, HideCardTraffic, OpenCardTrafficOutside);
        if (Modules?.IsActive(ModuleIds.Ai) == true)
            yield return new("translate", Strings.Common_Translate, "translate", new(760, 460),
                BorrowCardTranslate, HideCardTranslate, () =>
                {
                    HideCardDesktop();
                    ReturnCardTranslate();
                    Present(SecondaryId.Translate, Strings.Common_Translate, TakeTranslateView, _ => { });
                });
        if (panels.ContainsKey("clipboard"))
            yield return new("clipboard", Strings.Common_Clipboard, "clipboard", new(372, 640),
                ShowCardClipboard, HideCardClipboard, () => { HideCardDesktop(); Command("show-clipboard"); });
    }
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
        ReleaseCardTranslate();
        ReleaseCardClipboard();
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
        if (!cardDesktopShown)
        {
            var window = Native.GetForegroundWindow();
            GetWindowThreadIdForProcess(window, out uint pid);
            var kind = new Pane.Services.Clipboard.WindowsPastePlatform().Class(window);
            cardPasteTarget = pid != 0 && pid != Environment.ProcessId && kind is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                ? (window, pid) : default;
        }
        foreach (var panel in panels.Values) panel.Hide();
        var display = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
        if (cardDesktopWindow == null)
        {
            var view = new DesktopCardsView(CardDesktopCatalog, CreateCardDesktopContent, SaveCardDesktopPlacement,
                HideCardDesktop, OpenCardOutside, SetCardDesktopCardsVisible,
                key => CardTools().FirstOrDefault(tool => tool.Key == key)?.Size,
                CardDesktopButtons, CreateCardDesktopNote,
                () => { HideCardDesktop(); OpenSettings(null); }, LocateCardTool);
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
        cardDesktopCardsVisible = true;
        cardDesktopWorkArea = display.WorkArea;
        cardDesktopView!.RequestedTheme = Glass.Dark ? ElementTheme.Dark : ElementTheme.Light;
        cardDesktopWindow.Show(display.WorkArea, activate: !demo);
        RefreshDesktopCardsOverview();
        cardDesktopView.SetVisible(true);
        cardDesktopView.SetCardsVisible(true);
        cardDesktopView.FocusEntry();
        desktopTrigger?.Refresh();
    }

    void HideCardDesktop()
    {
        if (!cardDesktopShown) return;
        cardDesktopShown = false;
        cardDesktopView!.SetVisible(false);
        foreach (var tool in CardTools()) tool.Detach();
        cardDesktopWindow!.Hide();
        if (restoreOrdinaryTraffic) ReturnCardDesktopTraffic();
        if (restoreTranslateWindow) ReturnCardTranslate();
        desktopTrigger?.Refresh();
    }

    void SetCardDesktopCardsVisible(bool visible)
    {
        if (cardDesktopCardsVisible == visible) return;
        cardDesktopCardsVisible = visible;
        cardDesktopView!.SetCardsVisible(visible);
        foreach (var tool in CardTools())
        {
            if (!visible) tool.Detach();
            else if (Store.Current.CardDesktopLayout!.ContainsKey(tool.Key)) tool.Attach();
        }
    }

    void RefreshDesktopCardsOverview()
    {
        if (closing || cardDesktopView == null || Store.Current.CardDesktopLayout is not { } layout) return;
        var tools = CardTools().ToDictionary(tool => tool.Key);
        if (!layout.ContainsKey("traffic") || !tools.ContainsKey("traffic")) ReturnCardDesktopTraffic();
        if (!layout.ContainsKey("translate") || !tools.ContainsKey("translate")) ReturnCardTranslate();
        if (!layout.ContainsKey("clipboard") || !tools.ContainsKey("clipboard")) ReleaseCardClipboard();
        cardDesktopView.Apply(layout);
        if (cardDesktopShown && cardDesktopCardsVisible)
            foreach (var tool in tools.Values.Where(tool => layout.ContainsKey(tool.Key))) tool.Attach();
    }

    IEnumerable<(string Key, string Icon, string Title)> CardDesktopButtons()
    {
        if (desktopNotesEnabled) yield return ("note:", "pencil", Strings.Module_Notes_Name);
        if (desktopScreenshotsEnabled) yield return ("screenshot:", "image", Strings.Module_Screenshots_Name);
        if (desktopTrafficEnabled) yield return ("hud", "pulse", Strings.Traffic_DesktopCard);
        foreach (var tool in CardTools()) yield return (tool.Key, tool.Icon, tool.Title);
    }

    void OpenCardOutside(string key)
    {
        if (key == "hud") ShowTraffic();
        else if (key == "screenshot:") ShowScreenshots();
        else if (key == "note:") ShowNotes();
        else if (key.StartsWith("note:", StringComparison.Ordinal)) ShowNotes(Guid.Parse(key.AsSpan(5)));
        else if (key.StartsWith("screenshot:", StringComparison.Ordinal)) ShowScreenshots(Guid.Parse(key.AsSpan(11)));
        else CardTools().Single(tool => tool.Key == key).OpenOutside();
    }

    Guid? CreateCardDesktopNote()
    {
        Guid? id = null;
        HandleNoteLayout(() => id = GetNotes().Create("", "").Id);
        return id;
    }

    IEnumerable<(string Key, string Title)> CardDesktopCatalog()
    {
        if (desktopTrafficEnabled)
        {
            yield return ("hud", Strings.Traffic_DesktopCard);
        }
        foreach (var tool in CardTools()) yield return (tool.Key, tool.Title);
        if (desktopNotesEnabled)
        {
            var store = GetNotes();
            store.Load();
            foreach (var note in store.Documents)
                yield return ($"note:{note.Id:D}", string.IsNullOrWhiteSpace(note.Title) ? Strings.Notes_New : note.Title);
        }
        if (desktopScreenshotsEnabled)
            foreach (var screenshot in GetScreenshots().List())
                yield return ($"screenshot:{screenshot.Id:D}", $"{Strings.Module_Screenshots_Name} · {screenshot.CreatedUtc.ToLocalTime():g}");
    }

    FrameworkElement? CreateCardDesktopContent(string key)
    {
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

    nint CardPasteWindow()
    {
        var (window, process) = cardPasteTarget;
        GetWindowThreadIdForProcess(window, out uint current);
        return current != 0 && current == process && Native.IsWindowVisible(window) ? window : 0;
    }

    void ShowCardClipboard()
    {
        if (cardToolWindows.TryGetValue("clipboard", out var existing) && existing.AppWindow.IsVisible) return;
        if (cardClipboard == null)
        {
            var view = new ClipboardView(clipboardBridge) { WorkspacePasteTarget = CardPasteWindow, WorkspacePasteStarting = HideCardDesktop };
            view.CloseRequested += (_, _) => SaveCardDesktopPlacement("clipboard", null);
            view.SendToLauncher += (_, entry) =>
            {
                HideCardDesktop();
                if (AllowModule(ModuleIds.Launcher)) { launcherView?.AttachNext(entry); Command("show-launcher"); }
            };
            cardClipboard = view;
        }
        var clipboard = cardClipboard;
        var minimum = new Windows.Foundation.Size(clipboard.PreferredSize.Width, 0);
        var window = existing ?? CreateCardToolWindow("clipboard", Strings.Common_Clipboard, clipboard, clipboard.WorkspaceCaption, minimum);
        clipboard.ResumeWorkspace();
        ShowCardToolWindow("clipboard", window, minimum);
        clipboard.OnShown();
    }

    void HideCardClipboard()
    {
        if (cardToolWindows.TryGetValue("clipboard", out var window) && window.AppWindow.IsVisible)
        {
            window.AppWindow.Hide();
            cardClipboard!.OnHidden();
        }
    }

    void ReleaseCardClipboard()
    {
        CloseCardToolWindow("clipboard");
        cardClipboard?.Dispose();
        cardClipboard = null;
    }

    TranslateView TakeTranslateView()
    {
        var view = retainedTranslate;
        retainedTranslate = null;
        return view ?? CreateTranslateView();
    }

    void BorrowCardTranslate()
    {
        if (cardToolWindows.TryGetValue("translate", out var existing))
        {
            if (existing.AppWindow.IsVisible) return;
            if (!secondaries.ContainsKey(SecondaryId.Translate))
            {
                var cached = (TranslateView)((Border)existing.Content).Child;
                ShowCardToolWindow("translate", existing, cached.MinimumSize);
                return;
            }
            ReleaseCardTranslate();
        }
        TranslateView view;
        if (secondaries.Remove(SecondaryId.Translate, out var panel))
        {
            view = (TranslateView)panel.View;
            restoreTranslateWindow = panel.Shown;
            secondaryBounds[SecondaryId.Translate] = (panel.Origin.X, panel.Origin.Y, panel.Width, panel.Height);
            if (secondaryRelease.Remove(SecondaryId.Translate, out var release)) release();
            if (current == panel) current = null;
            panel.Dispose(releaseView: false);
        }
        else view = TakeTranslateView();
        view.CloseRequested += CloseCardTranslate;
        view.SetWorkspaceMode(true);
        var window = CreateCardToolWindow("translate", Strings.Common_Translate, view, view.WorkspaceCaption, view.MinimumSize);
        ShowCardToolWindow("translate", window, view.MinimumSize);
    }

    void HideCardTranslate()
    {
        if (cardToolWindows.TryGetValue("translate", out var window)) window.AppWindow.Hide();
    }

    void ReturnCardTranslate()
    {
        if (CloseCardToolWindow("translate") is not TranslateView view) return;
        view.CloseRequested -= CloseCardTranslate;
        view.SetWorkspaceMode(false);
        retainedTranslate = view;
        if (restoreTranslateWindow && !closing)
            Present(SecondaryId.Translate, Strings.Common_Translate, TakeTranslateView, _ => { }, activateWindow: false);
        restoreTranslateWindow = false;
    }

    void CloseCardTranslate(object? sender, EventArgs args)
    {
        SaveCardDesktopPlacement("translate", null);
        retainedTranslate?.Dispose();
        retainedTranslate = null;
    }

    void ReleaseCardTranslate()
    {
        restoreTranslateWindow = false;
        ReturnCardTranslate();
        retainedTranslate?.Dispose();
        retainedTranslate = null;
    }

    void LocateCardTool(string key)
    {
        if (key == "traffic") LocateCardTraffic();
        else cardToolWindows[key].Activate();
    }

    Window CreateCardToolWindow(string key, string title, UserControl content, FrameworkElement grip, Windows.Foundation.Size minimum)
    {
        var frame = new Border { Child = content, Style = (Style)Resources["PaneCardSurface"] };
        var window = new Window { Title = title, Content = frame };
        var appWindow = window.AppWindow;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var presenter = PrepareCardToolWindow(appWindow, hwnd);
        var caption = InputNonClientPointerSource.GetForWindowId(appWindow.Id);
        void UpdateCaption(object? sender, object? args)
        {
            if (grip.XamlRoot is not { } root) return;
            var scale = root.RasterizationScale;
            RectInt32 Region(FrameworkElement element)
            {
                var rect = element.TransformToVisual(frame).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
                return new((int)Math.Round(rect.X * scale), (int)Math.Round(rect.Y * scale),
                    (int)Math.Round(rect.Width * scale), (int)Math.Round(rect.Height * scale));
            }
            List<RectInt32> dragRegions = [Region(grip)];
            if (content is TranslateView translate)
            {
                // Explicit caption rectangles must stay disjoint from interactive controls.
                foreach (var control in translate.WorkspaceCaptionControls)
                {
                    var blocked = Region(control);
                    for (int i = dragRegions.Count - 1; i >= 0; i--)
                    {
                        var rect = dragRegions[i];
                        var right = rect.X + rect.Width;
                        var bottom = rect.Y + rect.Height;
                        var leftCut = Math.Max(rect.X, blocked.X);
                        var topCut = Math.Max(rect.Y, blocked.Y);
                        var rightCut = Math.Min(right, blocked.X + blocked.Width);
                        var bottomCut = Math.Min(bottom, blocked.Y + blocked.Height);
                        if (leftCut >= rightCut || topCut >= bottomCut) continue;
                        dragRegions.RemoveAt(i);
                        AddRegion(rect.X, rect.Y, leftCut, bottom);
                        AddRegion(rightCut, rect.Y, right, bottom);
                        AddRegion(leftCut, rect.Y, rightCut, topCut);
                        AddRegion(leftCut, bottomCut, rightCut, bottom);
                    }
                }
            }
            caption.SetRegionRects(NonClientRegionKind.Caption, dragRegions.ToArray());
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(minimum.Width * scale);
            var height = content is ClipboardView clip ? clip.WorkspaceMinimumHeight : minimum.Height;
            if (height > 0) presenter.PreferredMinimumHeight = (int)Math.Ceiling(height * scale);

            void AddRegion(int left, int top, int right, int bottom)
            {
                if (left < right && top < bottom) dragRegions.Add(new(left, top, right - left, bottom - top));
            }
        }
        grip.LayoutUpdated += UpdateCaption;
        Native.SubclassProc subclass = (h, message, w, l, id, data) =>
        {
            if (message == 0x232) // WM_EXITSIZEMOVE
            {
                var work = cardDesktopWorkArea;
                var scale = CardWorkspaceScale;
                queue.TryEnqueue(() =>
                {
                    if (cardToolWindows.GetValueOrDefault(key) == window)
                        SaveCardToolBounds(key, appWindow, work, scale);
                });
            }
            return Native.DefSubclassProc(h, message, w, l);
        };
        if (!Native.SetWindowSubclass(hwnd, subclass, 1, 0)) throw new System.ComponentModel.Win32Exception();
        appWindow.Closing += (_, args) =>
        {
            if (cardToolWindows.GetValueOrDefault(key) != window) return;
            args.Cancel = true;
            queue.TryEnqueue(() => SaveCardDesktopPlacement(key, null));
        };
        window.Closed += (_, _) =>
        {
            grip.LayoutUpdated -= UpdateCaption;
            Native.RemoveWindowSubclass(hwnd, subclass, 1);
        };
        cardToolWindows.Add(key, window);
        return window;
    }

    void ShowCardToolWindow(string key, Window window, Windows.Foundation.Size minimum)
    {
        ((Border)window.Content).RequestedTheme = cardDesktopView!.RequestedTheme;
        var appWindow = window.AppWindow;
        var presenter = (OverlappedPresenter)appWindow.Presenter;
        var scale = CardWorkspaceScale;
        presenter.PreferredMinimumWidth = (int)Math.Ceiling(minimum.Width * scale);
        if (minimum.Height > 0) presenter.PreferredMinimumHeight = (int)Math.Ceiling(minimum.Height * scale);
        PositionCardToolWindow(appWindow, cardDesktopWorkArea, scale, Store.Current.CardDesktopLayout![key], minimum);
        CardDesktopOverlay.ShowToolWindow(window);
        SetCardToolZOrder(WinRT.Interop.WindowNative.GetWindowHandle(window), true);
    }

    UserControl? CloseCardToolWindow(string key)
    {
        if (!cardToolWindows.Remove(key, out var window)) return null;
        var frame = (Border)window.Content;
        var view = (UserControl)frame.Child;
        frame.Child = null;
        window.Close();
        return view;
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
