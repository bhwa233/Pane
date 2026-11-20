using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Notes;
using Windows.Foundation;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Pane.Views;

public sealed partial class NotesView : UserControl, ISecondaryView, IDisposable
{
    readonly NoteStore store;
    readonly Func<Guid,bool> isPinned;
    readonly Action<Guid> togglePin, removed;
    readonly Func<Guid, DateTimeOffset?> getReminder;
    readonly Func<Guid, IReadOnlyDictionary<string, DateTimeOffset>?> getTaskReminders;
    readonly Action<Guid, string?, DateTimeOffset?> setReminder;
    readonly DispatcherQueueTimer autosave;
    Guid? historyNoteId;
    Guid? previewedVersionId;
    string? previewedVersionHash;
    bool ready, updating, disposed;

    internal NotesView(NoteStore store, Func<Guid,bool> isPinned, Action<Guid> togglePin, Action<Guid> removed,
        Func<Guid, DateTimeOffset?> getReminder, Func<Guid, IReadOnlyDictionary<string, DateTimeOffset>?> getTaskReminders,
        Action<Guid, string?, DateTimeOffset?> setReminder)
    {
        this.store = store;
        this.isPinned = isPinned;
        this.togglePin = togglePin;
        this.removed = removed;
        this.getReminder = getReminder;
        this.getTaskReminders = getTaskReminders;
        this.setReminder = setReminder;
        InitializeComponent();
        TitleBox.MaxLength = NoteStore.TitleLimit;
        BodyBox.MaxLength = NoteStore.BodyLimit;
        UpdateDocumentActions();
        autosave = DispatcherQueue.CreateTimer();
        autosave.Interval = TimeSpan.FromMilliseconds(600);
        autosave.IsRepeating = false;
        autosave.Tick += AutosaveTick;
        Shortcut(VirtualKey.N, NewNote);
        Shortcut(VirtualKey.S, () => TrySave());
        Shortcut(VirtualKey.W, () => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public Size PreferredSize => new(640, 420);
    public Size MinimumSize => new(470, 315);
    public UIElement DragRegion => Header;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler<bool>? ISecondaryView.TopmostChanged { add { } remove { } }
    public event EventHandler? CloseRequested;

    public void OnShowing(PanelShowContext context)
    {
        if (ready) return;
        try
        {
            store.Load();
            if (store.Draft == null) store.Select(store.Documents.FirstOrDefault()?.Id);
            ready = true;
            PresentDraft();
        }
        catch (Exception error) when (NoteStore.IsStorageError(error))
        {
            SearchBox.IsEnabled = NewButton.IsEnabled = TitleBox.IsEnabled = CategoryBox.IsEnabled = CategoryFilter.IsEnabled = BodyBox.IsEnabled = false;
            SaveButton.IsEnabled = DeleteButton.IsEnabled = PinButton.IsEnabled = false;
            Status.Text = Strings.Notes_LoadFailed;
        }
    }

    public void OnShown() { }
    public void OnHidden()
    {
        fileOperation?.Cancel();
        CustomReminderFlyout.Hide();
        SetPreview(false);
        CloseHistory();
        TrySave(refresh: false);
    }

    internal void OpenNote(Guid id)
    {
        OnShowing(new(0,null,false));
        if (!ready || store.Find(id) == null || !TrySave()) return;
        ExitRecycle();
        if (store.Draft?.Id == id)
        {
            CloseHistory();
            return;
        }
        store.Select(id);
        PresentDraft();
    }

    public void Dispose()
    {
        if (disposed) return;
        OnHidden();
        disposed = true;
        autosave.Tick -= AutosaveTick;
    }

    void Shortcut(VirtualKey key, Action action)
    {
        var binding = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        binding.Invoked += (_, args) => { args.Handled = true; action(); };
        KeyboardAccelerators.Add(binding);
    }

    void PresentDraft()
    {
        CloseHistory();
        SetPreview(false);
        if (splitView) SetSplit(false);
        undoStack.Clear();
        redoStack.Clear();
        lastSnapshot = DateTimeOffset.MinValue;
        updating = true;
        TitleBox.Text = store.Draft?.Title ?? "";
        CategoryBox.Text = store.Draft?.Category ?? "";
        BodyBox.Text = store.Draft?.Body ?? "";
        updating = false;
        RefreshNoteImages();
        SearchBox.IsEnabled = NewButton.IsEnabled = TitleBox.IsEnabled = CategoryBox.IsEnabled = CategoryFilter.IsEnabled = BodyBox.IsEnabled = true;
        UpdateButtons();
        ReloadCategories();
        ReloadList();
        Status.Text = store.HasChanges ? Strings.Common_NotSaved : StorageNotice();
        if (bodyScroller == null && (bodyScroller = FindScroller(BodyBox)) is { } scroller)
            scroller.ViewChanged += BodyScrollerChanged;
        RefreshToolbar();
    }

    string? SelectedCategory => (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string;

    void ReloadCategories()
    {
        var selected = SelectedCategory;
        updating = true;
        try
        {
            CategoryFilter.Items.Clear();
            CategoryFilter.Items.Add(CategoryItem(null, Strings.Notes_AllCategories));
            CategoryFilter.Items.Add(CategoryItem("", Strings.Notes_Uncategorized));
            foreach (var category in store.Documents.Select(note => note.Category).Where(category => category.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
                CategoryFilter.Items.Add(CategoryItem(category, category));
            CategoryFilter.SelectedItem = CategoryFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Tag as string, selected, StringComparison.OrdinalIgnoreCase)) ?? CategoryFilter.Items[0];
        }
        finally { updating = false; }
    }

    static ComboBoxItem CategoryItem(string? category, string label) => new()
    {
        Tag = category,
        Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 }
    };

    string StorageNotice() => store.UnreadableCount == 0 ? "" : string.Format(Strings.Notes_Unreadable, store.UnreadableCount);

    internal void RefreshSavedNotes()
    {
        if (ready) ReloadList();
    }

    void UpdateButtons()
    {
        SaveButton.IsEnabled = store.HasChanges;
        DeleteButton.IsEnabled = store.HasChanges || store.DraftIsSaved;
        HistoryButton.IsEnabled = store.DraftIsSaved;
        UpdateDocumentActions();
        RefreshPin();
        RefreshReminder();
    }

    internal void RefreshReminder(Guid? changedNote = null)
    {
        ReminderButton.IsEnabled = ready && store.DraftIsSaved;
        var due = store.Draft is { } draft ? getReminder(draft.Id) : null;
        var label = due is { } time ? string.Format(Strings.Notes_ReminderAt, time.ToLocalTime()) : Strings.Notes_Reminder;
        AutomationProperties.SetHelpText(ReminderButton, due.HasValue ? label : "");
        ToolTipService.SetToolTip(ReminderButton, label);
        if (changedNote is { } id && NoteList.Items.OfType<ListViewItem>().FirstOrDefault(item => Equals(item.Tag, id)) is { } row)
            RefreshReminderBadge(row, id);
    }

    static DateTimeOffset NextReminderEvening()
    {
        var now = DateTimeOffset.Now;
        var evening = now.Date.AddHours(18);
        var due = new DateTimeOffset(evening);
        return (due > now ? due : new DateTimeOffset(evening.AddDays(1))).ToUniversalTime();
    }

    static DateTimeOffset TomorrowReminderMorning() =>
        new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9)).ToUniversalTime();

    bool ChangeReminder(DateTimeOffset? due, string? task = null)
    {
        if (!store.DraftIsSaved || store.Draft is not { } draft) return false;
        try { setReminder(draft.Id, task, due); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Settings_SaveFailed; return false; }
        Status.Text = due is { } time ? string.Format(Strings.Notes_ReminderAt, time.ToLocalTime()) : Strings.Notes_ReminderCancelled;
        return true;
    }

    internal void RefreshPin()
    {
        PinButton.IsEnabled = ready && (store.HasChanges || store.DraftIsSaved);
        var pinned = store.Draft is { } draft && isPinned(draft.Id);
        PinIcon.Glyph = pinned ? "pin-off" : "pin";
        var label = pinned ? Strings.Notes_UnpinDesktop : Strings.Notes_PinDesktop;
        AutomationProperties.SetName(PinButton,label);
        ToolTipService.SetToolTip(PinButton,label);
    }

    void PinClicked(object sender, RoutedEventArgs args)
    {
        if (!TrySave() || !store.DraftIsSaved || store.Draft is not { } draft) return;
        try { togglePin(draft.Id); }
        catch (InvalidOperationException error) { Status.Text = error.Message; }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Notes_LayoutSaveFailed; }
        RefreshPin();
    }

    bool TrySave(bool refresh = true)
    {
        if (disposed || !ready || !store.HasChanges) return true;
        autosave.Stop();
        try { store.SaveDraft(); }
        catch (Exception error) when (NoteStore.IsStorageError(error))
        {
            Status.Text = Strings.Notes_SaveFailed;
            return false;
        }
        if (refresh) { ReloadCategories(); ReloadList(); }
        Status.Text = store.UnreadableCount == 0 ? Strings.Notes_Saved : StorageNotice();
        UpdateButtons();
        return true;
    }

    void NewNote()
    {
        if (!ready || !TrySave()) return;
        ExitRecycle();
        store.Select(null);
        PresentDraft();
        TitleBox.Focus(FocusState.Programmatic);
    }

    void SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready || updating || NoteList.SelectedItem is not ListViewItem { Tag: Guid id } || store.Draft?.Id == id) return;
        if (!TrySave(refresh: false)) { ReloadList(); return; }
        store.Select(id);
        PresentDraft();
    }

    void EditorChanged(object sender, TextChangedEventArgs args)
    {
        if (!ready || updating) return;
        PushUndoSnapshot(false);
        store.Edit(TitleBox.Text, BodyBox.Text, CategoryBox.Text);
        UpdateButtons();
        Status.Text = store.HasChanges ? Strings.Common_NotSaved : StorageNotice();
        RefreshCharCount();
        autosave.Stop();
        if (store.HasChanges) autosave.Start();
    }

    void DeleteClicked(object sender, RoutedEventArgs args)
    {
        autosave.Stop();
        var id = store.Draft?.Id;
        var saved = store.DraftIsSaved;
        try { store.RecycleDraft(); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Notes_DeleteFailed; return; }
        if (id is { } deleted) removed(deleted);
        store.Select(store.Documents.FirstOrDefault()?.Id);
        PresentDraft();
        if (saved) Status.Text = Strings.Notes_Recycled;
    }

    void HistoryClicked(object sender, RoutedEventArgs args)
    {
        if (!TrySave() || !store.DraftIsSaved || store.Draft is not { } draft) return;
        IReadOnlyList<DateTimeOffset> versions;
        try { versions = store.Versions(draft.Id); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Notes_HistoryFailed; return; }

        historyNoteId = draft.Id;
        HistoryVersions.Items.Clear();
        foreach (var version in versions)
            HistoryVersions.Items.Add(new ComboBoxItem { Tag = version, Content = version.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") });
        HistoryVersions.IsEnabled = versions.Count != 0;
        EditorPane.Visibility = Visibility.Collapsed;
        HistoryPane.Visibility = Visibility.Visible;
        SetPreview(false);
        if (versions.Count > 0)
        {
            HistoryVersions.SelectedIndex = 0;
            HistoryVersions.Focus(FocusState.Programmatic);
        }
        else
        {
            HistoryStatus.Text = Strings.Notes_HistoryEmpty;
            HistoryBackButton.Focus(FocusState.Programmatic);
        }
    }

    void CloseHistory()
    {
        if (historyNoteId == null) return;
        historyNoteId = null;
        HistoryPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        HistoryVersions.Items.Clear();
        HistoryTitle.Text = HistoryCategory.Text = HistoryBody.Text = HistoryStatus.Text = "";
        ShowImages(HistoryImages, HistoryImageScroller, null);
        RestoreVersionButton.IsEnabled = false;
        UpdateDocumentActions();
    }

    void HistoryBackClicked(object sender, RoutedEventArgs args)
    {
        CloseHistory();
        HistoryButton.Focus(FocusState.Programmatic);
    }

    void HistorySelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (historyNoteId is not { } id) return;
        HistoryTitle.Text = HistoryCategory.Text = HistoryBody.Text = HistoryStatus.Text = "";
        ShowImages(HistoryImages, HistoryImageScroller, null);
        RestoreVersionButton.IsEnabled = false;
        if (HistoryVersions.SelectedItem is not ComboBoxItem { Tag: DateTimeOffset version }) return;
        try
        {
            var note = store.ReadVersion(id, version);
            // 指纹（galaxy expectedRevision 对齐）：预览时记下版本内容指纹，恢复时校验未被外部改动。
            previewedVersionId = id;
            previewedVersionHash = NoteStore.VersionHash(note);
            HistoryTitle.Text = string.IsNullOrWhiteSpace(note.Title) ? Strings.Notes_Untitled : note.Title;
            HistoryCategory.Text = note.Category.Length == 0 ? Strings.Notes_Uncategorized : note.Category;
            HistoryBody.Text = note.Body;
            ShowImages(HistoryImages, HistoryImageScroller, note);
            RestoreVersionButton.IsEnabled = true;
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { HistoryStatus.Text = Strings.Notes_HistoryFailed; }
    }

    void RestoreVersionClicked(object sender, RoutedEventArgs args)
    {
        if (historyNoteId is not { } id || HistoryVersions.SelectedItem is not ComboBoxItem { Tag: DateTimeOffset version }) return;
        var expected = previewedVersionId == id ? previewedVersionHash : null;
        try
        {
            if (!store.RestoreVersion(id, version, expected)) { HistoryStatus.Text = Strings.Notes_RestoreChanged; return; }
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { HistoryStatus.Text = Strings.Notes_RestoreFailed; return; }
        PresentDraft();
        Status.Text = Strings.Notes_Restored;
        HistoryButton.Focus(FocusState.Programmatic);
    }

    void SearchChanged(object sender, TextChangedEventArgs args) { if (ready && !updating) ReloadList(); }
    void CategoryFilterChanged(object sender, SelectionChangedEventArgs args) { if (ready && !updating) ReloadList(); }
    void AutosaveTick(DispatcherQueueTimer sender, object? args)
    {
        // 分栏开着时借同一节流时点刷新右侧预览（600ms 静默），不逐键重建。
        if (splitView) BuildSplitPreview();
        TrySave();
    }
    void SaveClicked(object sender, RoutedEventArgs args) => TrySave();
    void NewClicked(object sender, RoutedEventArgs args) => NewNote();
    void CloseClicked(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
