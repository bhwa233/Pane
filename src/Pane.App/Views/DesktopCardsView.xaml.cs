using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Notes;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

public sealed partial class DesktopCardsView : UserControl, ISecondaryView, IDisposable
{
    enum Kind { Traffic, Note, Screenshot }
    readonly Dictionary<(Kind Kind, Guid Id), Border> cards = [];
    readonly GlassParameters appearance;
    readonly Func<DesktopTrafficView> traffic;
    readonly Func<NoteDocument, DesktopNoteView> note;
    readonly Func<Guid, DesktopScreenshotView> screenshot;
    readonly Action openTraffic, openNotes, openScreenshots, toggleVisibility;
    readonly Func<bool> beginArrangement;
    readonly Func<string?> saveArrangement;
    readonly Action cancelArrangement;
    bool shown, arranging, hasPanels, watchingAppearance;
    CornerRadius cardRadius;

    internal DesktopCardsView(GlassParameters appearance, Func<DesktopTrafficView> traffic, Func<NoteDocument, DesktopNoteView> note,
        Func<Guid, DesktopScreenshotView> screenshot, Action openTraffic, Action openNotes, Action openScreenshots,
        Action toggleVisibility, Func<bool> beginArrangement, Func<string?> saveArrangement, Action cancelArrangement)
    {
        this.appearance = appearance;
        this.traffic = traffic;
        this.note = note;
        this.screenshot = screenshot;
        this.openTraffic = openTraffic;
        this.openNotes = openNotes;
        this.openScreenshots = openScreenshots;
        this.toggleVisibility = toggleVisibility;
        this.beginArrangement = beginArrangement;
        this.saveArrangement = saveArrangement;
        this.cancelArrangement = cancelArrangement;
        InitializeComponent();
        AppearanceChanged(null, EventArgs.Empty);
    }

    public Size PreferredSize => new(580, 438);
    public Size MinimumSize => new(360, 280);
    public UIElement DragRegion => Header;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler<bool>? ISecondaryView.TopmostChanged { add { } remove { } }
    public event EventHandler? CloseRequested;

    internal void Apply(bool trafficAvailable, bool notesAvailable, bool screenshotsAvailable, bool trafficPinned,
        IEnumerable<NoteDocument> notes, IEnumerable<Guid> screenshots)
    {
        TrafficButton.Visibility = Visible(trafficAvailable);
        NotesButton.Visibility = Visible(notesAvailable);
        ScreenshotsButton.Visibility = Visible(screenshotsAvailable);

        var active = new HashSet<(Kind, Guid)>();
        if (trafficPinned) Ensure((Kind.Traffic, Guid.Empty), traffic);
        foreach (var document in notes)
            ((DesktopNoteView)Ensure((Kind.Note, document.Id), () => note(document)).Child).Apply(document);
        foreach (var id in screenshots) Ensure((Kind.Screenshot, id), () => screenshot(id));
        foreach (var key in cards.Keys.Where(key => !active.Contains(key)).ToArray()) Remove(key);
        EmptyState.Visibility = Visible(cards.Count == 0);

        Border Ensure((Kind Kind, Guid Id) key, Func<FrameworkElement> create)
        {
            var index = active.Count;
            active.Add(key);
            if (cards.TryGetValue(key, out var frame)) return frame;
            var content = create();
            var size = ((IPanelView)content).PreferredSize;
            frame = new Border { Style = (Style)Resources["CardFrame"], CornerRadius = cardRadius, Width = size.Width, Height = size.Height, Child = content };
            cards.Add(key, frame);
            Tiles.Children.Insert(Math.Min(index, Tiles.Children.Count), frame);
            if (shown && content is DesktopTrafficView live) live.OnShown();
            return frame;
        }
    }

    void Remove((Kind Kind, Guid Id) key)
    {
        if (!cards.Remove(key, out var frame)) return;
        (frame.Child as IDisposable)?.Dispose();
        Tiles.Children.Remove(frame);
        frame.Child = null;
    }

    internal void UpdateScreenshot(Guid id, string path)
    {
        if (cards.TryGetValue((Kind.Screenshot, id), out var frame)) ((DesktopScreenshotView)frame.Child).Apply(path);
    }

    internal void ApplyDesktopVisibility(bool hasCards, bool hidden)
    {
        VisibilityButton.IsEnabled = hasCards || hidden;
        VisibilityIcon.Glyph = hidden ? "eye" : "eye-off";
        var name = hidden ? Strings.DesktopCards_Show : Strings.DesktopCards_Hide;
        AutomationProperties.SetName(VisibilityButton, name);
        ToolTipService.SetToolTip(VisibilityButton, name);
    }

    internal void ApplyArrangement(bool available, bool editing)
    {
        var restoreFocus = arranging && !editing && XamlRoot is { } root &&
            FocusManager.GetFocusedElement(root) is Button focused &&
            (focused == SaveArrangementButton || focused == CancelArrangementButton);
        hasPanels = available;
        arranging = editing;
        HeaderTitle.Text = editing ? Strings.DesktopCards_Arrange : Strings.DesktopCards_Title;
        ViewTools.Visibility = Visible(!editing);
        ArrangeTools.Visibility = Visible(editing);
        ArrangeButton.IsEnabled = available;
        if (!editing) ArrangementError.Visibility = Visibility.Collapsed;
        if (restoreFocus) (available ? ArrangeButton : CloseButton).Focus(FocusState.Programmatic);
    }

    void EditArrangementClicked(object sender, RoutedEventArgs args)
    {
        if (!beginArrangement()) return;
        ApplyArrangement(hasPanels, true);
        SaveArrangementButton.Focus(FocusState.Programmatic);
    }

    void SaveArrangementClicked(object sender, RoutedEventArgs args)
    {
        if (!arranging) return;
        if (saveArrangement() is { } error)
        {
            ArrangementError.Text = error;
            ArrangementError.Visibility = Visibility.Visible;
            return;
        }
        ApplyArrangement(hasPanels, false);
    }

    void CancelArrangementEdit()
    {
        if (!arranging) return;
        cancelArrangement();
        ApplyArrangement(hasPanels, false);
    }

    void CancelArrangementClicked(object sender, RoutedEventArgs args) => CancelArrangementEdit();

    void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!arranging || args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        CancelArrangementEdit();
    }

    static Visibility Visible(bool yes) => yes ? Visibility.Visible : Visibility.Collapsed;

    public void OnShowing(PanelShowContext context)
    {
        if (watchingAppearance) return;
        watchingAppearance = true;
        appearance.Changed += AppearanceChanged;
        AppearanceChanged(null, EventArgs.Empty);
    }

    void AppearanceChanged(object? sender, EventArgs args)
    {
        var radius = (CornerRadius)Application.Current.Resources["PaneRadiusItem"];
        if (radius.Equals(cardRadius)) return;
        cardRadius = radius;
        foreach (var frame in cards.Values) frame.CornerRadius = radius;
    }

    public void OnShown()
    {
        shown = true;
        if (cards.TryGetValue((Kind.Traffic, Guid.Empty), out var frame)) ((DesktopTrafficView)frame.Child).OnShown();
    }

    public void OnHidden() => Dispose();

    public void Dispose()
    {
        if (watchingAppearance)
        {
            appearance.Changed -= AppearanceChanged;
            watchingAppearance = false;
        }
        if (arranging) { arranging = false; cancelArrangement(); }
        shown = false;
        foreach (var key in cards.Keys.ToArray()) Remove(key);
    }

    void TrafficClicked(object sender, RoutedEventArgs args) => openTraffic();
    void ToggleVisibilityClicked(object sender, RoutedEventArgs args) => toggleVisibility();
    void NotesClicked(object sender, RoutedEventArgs args) => openNotes();
    void ScreenshotsClicked(object sender, RoutedEventArgs args) => openScreenshots();
    void CloseClicked(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
