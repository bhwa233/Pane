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

/// <summary>便签管理器（notes-mvp §3）：列表、搜索、分类、纯文本编辑、历史、回收与提醒。
/// 编辑走共享会话（NoteSessions）：桌面便签卡与这里编辑同一条草稿，任一处输入另一处立即更新。</summary>
public sealed partial class NotesView : UserControl, ISecondaryView, IDisposable
{
    readonly NoteStore store;
    readonly NoteSessions sessions;
    readonly Func<Guid,bool> isPinned;
    readonly Action<Guid> togglePin;
    readonly Func<Guid, NoteDeleteSnapshot?> captureDelete;
    readonly Action<Guid> recycleNote;
    readonly Action<NoteDeleteSnapshot> undoDelete;
    readonly Func<Guid, DateTimeOffset?> getReminder;
    readonly Action<Guid, DateTimeOffset?> setReminder;
    readonly DispatcherQueueTimer undoWindow;
    NoteSessions.Session? current;
    Guid? historyNoteId;
    long historyRevision;
    Guid? previewedVersionId;
    string? previewedVersionHash;
    NoteDeleteSnapshot? pendingUndo;
    bool ready, updating, disposed;

    internal NotesView(NoteStore store, NoteSessions sessions, Func<Guid,bool> isPinned, Action<Guid> togglePin,
        Func<Guid, NoteDeleteSnapshot?> captureDelete, Action<Guid> recycleNote, Action<NoteDeleteSnapshot> undoDelete,
        Func<Guid, DateTimeOffset?> getReminder, Action<Guid, DateTimeOffset?> setReminder)
    {
        this.store = store;
        this.sessions = sessions;
        this.isPinned = isPinned;
        this.togglePin = togglePin;
        this.captureDelete = captureDelete;
        this.recycleNote = recycleNote;
        this.undoDelete = undoDelete;
        this.getReminder = getReminder;
        this.setReminder = setReminder;
        InitializeComponent();
        TitleBox.MaxLength = NoteStore.TitleLimit;
        BodyBox.MaxLength = NoteStore.BodyLimit;
        UpdateDocumentActions();
        sessions.Changed += SessionChanged;
        sessions.Saved += SessionSaved;
        sessions.SaveFailed += SessionSaveFailed;
        undoWindow = DispatcherQueue.CreateTimer();
        undoWindow.Interval = TimeSpan.FromSeconds(6);
        undoWindow.IsRepeating = false;
        undoWindow.Tick += UndoWindowElapsed;
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
            if (current == null) SelectFirst();
            ready = true;
            PresentSession();
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
        CloseHistory();
        HideUndo();
        // 管理器隐藏前结清当前会话（notes-mvp §2「隐藏」入口）。
        TrySave();
    }

    /// <summary>托盘 / 桌面卡总览深开：定位到指定便签。</summary>
    internal void OpenNote(Guid id)
    {
        OnShowing(new(0,null,false));
        if (!ready || !TrySave()) return;
        ExitRecycle();
        if (current?.Id == id)
        {
            CloseHistory();
            return;
        }
        try { Switch(sessions.Open(id)); }
        catch (KeyNotFoundException) { return; }
        PresentSession();
    }

    public void Dispose()
    {
        if (disposed) return;
        OnHidden();
        disposed = true;
        sessions.Changed -= SessionChanged;
        sessions.Saved -= SessionSaved;
        sessions.SaveFailed -= SessionSaveFailed;
        undoWindow.Tick -= UndoWindowElapsed;
        if (current is { } session)
        {
            sessions.Detach(session);
            current = null;
        }
    }

    void Shortcut(VirtualKey key, Action action)
    {
        var binding = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        binding.Invoked += (_, args) => { args.Handled = true; action(); };
        KeyboardAccelerators.Add(binding);
    }

    /// <summary>切换绑定会话：旧的解绑（引用归零且已结清才释放），新的挂接并计数。</summary>
    void Switch(NoteSessions.Session next)
    {
        if (current?.Id == next.Id) { current = next; return; }
        if (current is { } previous) sessions.Detach(previous);
        current = next;
        sessions.Attach(next);
    }

    void Unbind()
    {
        if (current is { } previous) sessions.Detach(previous);
        current = null;
    }

    void SelectFirst()
    {
        try { Switch(sessions.Open(store.Documents.FirstOrDefault()?.Id ?? Guid.Empty)); }
        catch (KeyNotFoundException) { Switch(sessions.New()); }
    }

    /// <summary>把当前会话呈现到编辑器（程序赋值不回发共享草稿）。</summary>
    void PresentSession()
    {
        CloseHistory();
        if (current is not { } session) return;
        updating = true;
        try
        {
            TitleBox.Text = session.Title;
            CategoryBox.Text = session.Category;
            BodyBox.Text = session.Body;
        }
        finally { updating = false; }
        SearchBox.IsEnabled = NewButton.IsEnabled = TitleBox.IsEnabled = CategoryBox.IsEnabled = CategoryFilter.IsEnabled = BodyBox.IsEnabled = true;
        UpdateButtons();
        ReloadCategories();
        ReloadList();
        RefreshStatus();
        RefreshCharCount();
    }

    void RefreshStatus()
    {
        if (current is not { } session) return;
        Status.Text = session.Failed ? Strings.Notes_SaveFailed
            : session.Revision != session.SavedRevision ? Strings.Common_NotSaved
            : store.UnreadableCount == 0 ? Strings.Notes_Saved : StorageNotice();
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

    void RefreshCharCount()
    {
        CharCount.Text = current is { Body.Length: > 0 } session
            ? string.Format(Strings.Notes_CharCount, (object?)session.Body.Length.ToString("N0")) : "";
    }

    string StorageNotice() => store.UnreadableCount == 0 ? "" : string.Format(Strings.Notes_Unreadable, store.UnreadableCount);

    /// <summary>外部（导入 / 桌面保存 / 恢复）后的列表刷新。</summary>
    internal void RefreshSavedNotes()
    {
        if (ready) ReloadList();
    }

    void UpdateButtons()
    {
        var session = current;
        SaveButton.IsEnabled = session is { Failed: true } or { Persisted: false, IsEmpty: false } || session is { } active && active.Revision != active.SavedRevision;
        DeleteButton.IsEnabled = session != null && (session.Persisted || !session.IsEmpty);
        HistoryButton.IsEnabled = session is { Persisted: true };
        UpdateDocumentActions();
        RefreshPin();
        RefreshReminder();
    }

    internal void RefreshReminder(Guid? changedNote = null)
    {
        ReminderButton.IsEnabled = ready && current is { Persisted: true };
        var due = current is { } session ? getReminder(session.Id) : null;
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

    bool ChangeReminder(DateTimeOffset? due)
    {
        if (current is not { Persisted: true } session) return false;
        try { setReminder(session.Id, due); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Settings_SaveFailed; return false; }
        Status.Text = due is { } time ? string.Format(Strings.Notes_ReminderAt, time.ToLocalTime()) : Strings.Notes_ReminderCancelled;
        return true;
    }

    internal void RefreshPin()
    {
        PinButton.IsEnabled = ready && current is { Persisted: true };
        var pinned = current is { } session && isPinned(session.Id);
        PinIcon.Glyph = pinned ? "pin-off" : "pin";
        var label = pinned ? Strings.Notes_UnpinDesktop : Strings.Notes_PinDesktop;
        AutomationProperties.SetName(PinButton,label);
        ToolTipService.SetToolTip(PinButton,label);
    }

    void PinClicked(object sender, RoutedEventArgs args)
    {
        if (!TrySave() || current is not { Persisted: true } session) return;
        try { togglePin(session.Id); }
        catch (InvalidOperationException error) { Status.Text = error.Message; }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Notes_LayoutSaveFailed; }
        RefreshPin();
        ReloadList();
    }

    /// <summary>共享会话保存入口：显式保存、隐藏、切换、删除和恢复前都走这里。</summary>
    bool TrySave()
    {
        if (disposed || !ready || current is not { } session) return true;
        if (!sessions.Flush(session))
        {
            Status.Text = Strings.Notes_SaveFailed;
            return false;
        }
        ReloadCategories();
        ReloadList();
        RefreshStatus();
        UpdateButtons();
        return true;
    }

    void NewNote()
    {
        if (!ready || !TrySave()) return;
        ExitRecycle();
        Switch(sessions.New());
        PresentSession();
        TitleBox.Focus(FocusState.Programmatic);
    }

    void SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready || updating || NoteList.SelectedItem is not ListViewItem { Tag: Guid id } || current?.Id == id) return;
        if (!TrySave()) { ReloadList(); return; }
        try { Switch(sessions.Open(id)); }
        catch (KeyNotFoundException) { ReloadList(); return; }
        PresentSession();
    }

    void EditorChanged(object sender, TextChangedEventArgs args)
    {
        if (!ready || updating || current is not { } session) return;
        sessions.Edit(session, TitleBox.Text, BodyBox.Text, CategoryBox.Text, this);
        UpdateButtons();
        RefreshStatus();
        RefreshCharCount();
        RefreshRowTitle(session);
    }

    /// <summary>共享草稿被其他窗口更新（notes-mvp §2 实时同步）：程序赋值不回发。</summary>
    void SessionChanged(NoteSessions.Session session, object? source)
    {
        if (disposed || ReferenceEquals(source, this)) return;
        if (current?.Id == session.Id)
        {
            updating = true;
            try
            {
                if (TitleBox.Text != session.Title) TitleBox.Text = session.Title;
                if (CategoryBox.Text != session.Category) CategoryBox.Text = session.Category;
                if (BodyBox.Text != session.Body) BodyBox.Text = session.Body;
            }
            finally { updating = false; }
            RefreshStatus();
            RefreshCharCount();
            UpdateButtons();
        }
        RefreshRowTitle(session);
    }

    void SessionSaved(NoteSessions.Session session)
    {
        if (disposed) return;
        if (current?.Id == session.Id)
        {
            RefreshStatus();
            UpdateButtons();
        }
        RefreshRowTitle(session);
    }

    void SessionSaveFailed(NoteSessions.Session session)
    {
        if (disposed) return;
        if (current?.Id == session.Id) Status.Text = Strings.Notes_SaveFailed;
    }

    /// <summary>只刷新相关行（notes-mvp §3）：编辑当前项期间不重建列表、不重排序。</summary>
    void RefreshRowTitle(NoteSessions.Session session)
    {
        if (historyNoteId != null || !ready) return;
        var row = NoteList.Items.OfType<ListViewItem>().FirstOrDefault(item => Equals(item.Tag, session.Id));
        if (row?.Content is not Grid shell || shell.Children.OfType<StackPanel>().FirstOrDefault() is not { } content) return;
        if (FindRowTitle(content) is not { } title) return;
        var text = string.IsNullOrWhiteSpace(session.Title) ? Strings.Notes_Untitled : session.Title;
        if (title.Text != text) title.Text = text;
    }

    // ---------------- 删除与撤销（notes-mvp §4） ----------------

    void DeleteClicked(object sender, RoutedEventArgs args)
    {
        if (current is not { } session) return;
        DeleteNote(session);
    }

    void DeleteNote(NoteSessions.Session session)
    {
        if (session.Persisted && !TrySave()) return;
        NoteDeleteSnapshot? snapshot = null;
        if (session.Persisted)
        {
            snapshot = captureDelete(session.Id);
            recycleNote(session.Id);
        }
        else sessions.Forget(session.Id);
        Unbind();
        SelectFirst();
        PresentSession();
        if (snapshot is { } undo) ShowUndo(undo);
        else HideUndo();
        Status.Text = snapshot is null ? "" : Strings.Notes_Recycled;
    }

    void ShowUndo(NoteDeleteSnapshot snapshot)
    {
        pendingUndo = snapshot;
        UndoDeleteButton.Content = Strings.Notes_UndoDelete;
        UndoDeleteButton.Visibility = Visibility.Visible;
        undoWindow.Stop();
        undoWindow.Start();
    }

    void UndoWindowElapsed(DispatcherQueueTimer sender, object? args) => HideUndo();

    void HideUndo()
    {
        undoWindow.Stop();
        UndoDeleteButton.Visibility = Visibility.Collapsed;
        pendingUndo = null;
    }

    void UndoDeleteClicked(object sender, RoutedEventArgs args)
    {
        if (pendingUndo is not { } snapshot) { HideUndo(); return; }
        HideUndo();
        undoDelete(snapshot);
        ReloadCategories();
        ReloadList();
        try { Switch(sessions.Open(snapshot.Id)); }
        catch (KeyNotFoundException) { SelectFirst(); }
        PresentSession();
        Status.Text = Strings.Notes_RecycleRestored;
    }

    // ---------------- 历史（notes-mvp §4） ----------------

    void HistoryClicked(object sender, RoutedEventArgs args)
    {
        if (!TrySave() || current is not { Persisted: true } session) return;
        IReadOnlyList<DateTimeOffset> versions;
        try { versions = store.Versions(session.Id); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Notes_HistoryFailed; return; }

        historyNoteId = session.Id;
        historyRevision = session.Revision;
        HistoryVersions.Items.Clear();
        foreach (var version in versions)
            HistoryVersions.Items.Add(new ComboBoxItem { Tag = version, Content = version.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") });
        HistoryVersions.IsEnabled = versions.Count != 0;
        EditorPane.Visibility = Visibility.Collapsed;
        HistoryPane.Visibility = Visibility.Visible;
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
            RestoreVersionButton.IsEnabled = true;
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { HistoryStatus.Text = Strings.Notes_HistoryFailed; }
    }

    void RestoreVersionClicked(object sender, RoutedEventArgs args)
    {
        if (historyNoteId is not { } id || HistoryVersions.SelectedItem is not ComboBoxItem { Tag: DateTimeOffset version } || current is not { } session) return;
        // 恢复前先结清共享草稿，再复核历史页打开时的内容修订；期间内容变化则拒绝，避免用过时的预览覆盖刚输入的字。
        if (!TrySave() || sessions.Find(id) is not { } live || live.Revision != historyRevision)
        {
            HistoryStatus.Text = Strings.Notes_RestoreChanged;
            return;
        }
        var expected = previewedVersionId == id ? previewedVersionHash : null;
        NoteDocument previous;
        try
        {
            previous = store.ReadVersion(id, version);
            if (expected is { } hash && !string.Equals(expected, hash, StringComparison.Ordinal) &&
                !string.Equals(hash, NoteStore.VersionHash(previous), StringComparison.Ordinal))
            {
                HistoryStatus.Text = Strings.Notes_RestoreChanged;
                return;
            }
            store.Restore(id, previous);
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { HistoryStatus.Text = Strings.Notes_RestoreFailed; return; }
        // 恢复保留当前分类；所有编辑器和胶囊立即显示同一内容。
        live.Title = previous.Title;
        live.Body = previous.Body;
        live.Revision++;
        live.SavedRevision = live.Revision;
        live.SavedBody = previous.Body;
        live.BodyTouched = false;
        live.HistoryRecorded = true;
        live.LastHistoryAt = DateTimeOffset.UtcNow;
        sessions.BroadcastRestored(live);
        PresentSession();
        Status.Text = Strings.Notes_Restored;
        HistoryButton.Focus(FocusState.Programmatic);
    }

    void SearchChanged(object sender, TextChangedEventArgs args) { if (ready && !updating) ReloadList(); }
    void CategoryFilterChanged(object sender, SelectionChangedEventArgs args) { if (ready && !updating) ReloadList(); }
    void SaveClicked(object sender, RoutedEventArgs args) => TrySave();
    void NewClicked(object sender, RoutedEventArgs args) => NewNote();
    void CloseClicked(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
