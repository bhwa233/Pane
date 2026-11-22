using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Views.Controls;

namespace Pane.Views;

public sealed partial class NotesView
{
    void ReloadList()
    {
        var query = SearchBox.Text.Trim();
        updating = true;
        try
        {
            NoteList.Items.Clear();
            var matches = store.Documents.Where(note =>
                (selectedTags.Count == 0 || note.EffectiveTags.Any(selectedTags.Contains)) &&
                (query.Length == 0 || note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                 note.Body.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
            foreach (var note in matches)
            {
                var row = NoteRow(note, note.Id);
                RefreshReminderBadge(row, note.Id);
                NoteList.Items.Add(row);
                if (current?.Id == note.Id) NoteList.SelectedItem = row;
            }
            ListCount.Text = string.Format(Strings.Notes_ListCount, matches.Length);
            EmptyList.Text = matches.Length > 0 ? "" : query.Length == 0 && selectedTags.Count == 0 ? Strings.Notes_Empty : Strings.Notes_NoResults;
        }
        finally { updating = false; }
    }

    ListViewItem NoteRow(NoteDocument note, object tag)
    {
        var title = string.IsNullOrWhiteSpace(note.Title) ? Strings.Notes_Untitled : note.Title;
        var time = note.Updated.ToLocalTime();
        var content = new StackPanel { Spacing = 5 };
        var heading = new Grid { ColumnSpacing = 6 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        var date = new TextBlock { Text = time.ToString("MM-dd"), Foreground = Status.Foreground, FontSize = Status.FontSize };
        Grid.SetColumn(date, 1);
        heading.Children.Add(date);
        content.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(note.Body))
            content.Children.Add(new TextBlock { Text = note.Body.Trim(), TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, Foreground = Status.Foreground });
        var labels = new WrapPanel { HorizontalSpacing = 4, IsHitTestVisible = false };
        if (isPinned(note.Id))
        {
            var accent = (SolidColorBrush)Application.Current.Resources["PaneAccentBrush"];
            var badge = Label(Strings.Notes_PinnedDesktop);
            badge.Background = new SolidColorBrush(accent.Color) { Opacity = 0.18 };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            line.Children.Add(new PaneIcon { Glyph = "pin", Width = 11, Height = 11, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
            var caption = badge.Child;
            badge.Child = null;
            line.Children.Add(caption);
            badge.Child = line;
            labels.Children.Add(badge);
        }
        foreach (var label in note.EffectiveTags) labels.Children.Add(Label(label));
        if (isHidden(note.Id)) labels.Children.Add(Label(Strings.Settings_Launcher_Hidden));
        if (labels.Children.Count > 0) content.Children.Add(labels);
        content.Children.Add(new TextBlock
        {
            Text = $"{time:HH:mm}  ·  {string.Format(Strings.Notes_CharCount, note.Body.Length.ToString("N0"))}",
            FontSize = Status.FontSize, Foreground = Status.Foreground
        });
        var shell = new Grid { ColumnSpacing = 6 };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shell.Children.Add(content);
        var row = new ListViewItem { Tag = tag, Content = shell, Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(12, 6, 12, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(row, title);
        row.RightTapped += (_, args) => ShowNoteMenu(row, note.Id, args);
        return row;

        Border Label(string text) => new()
        {
            Background = LabelFill.Background, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Status.Foreground, FontSize = Status.FontSize }
        };
    }

    // ---------------- 便签行菜单（右键） ----------------

    void ShowNoteMenu(ListViewItem row, Guid id, RightTappedRoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        if (isHidden(id))
        {
            // 已隐藏的便签：先找回，再谈其他操作（notes-mvp §6「隐藏与找回」）。
            var reveal = new MenuFlyoutItem { Text = Strings.Notes_RevealHidden };
            reveal.Click += (_, _) => revealNote(id);
            menu.Items.Add(reveal);
        }
        var pinned = isPinned(id);
        var pin = new MenuFlyoutItem { Text = pinned ? Strings.Notes_UnpinDesktop : Strings.Notes_PinDesktop };
        pin.Click += (_, _) => RowTogglePin(id);
        menu.Items.Add(pin);
        menu.Items.Add(RowReminderMenu(id));
        var history = new MenuFlyoutItem { Text = Strings.Notes_History };
        history.Click += (_, _) => OpenRowHistory(id);
        menu.Items.Add(history);
        var export = new MenuFlyoutItem { Text = Strings.Notes_ExportFile };
        export.Click += (_, _) => ExportRowFile(id);
        menu.Items.Add(export);
        var danger = new MenuFlyoutSubItem { Text = Strings.Common_Delete };
        var confirm = new MenuFlyoutItem { Text = Strings.Notes_DeleteRowConfirm };
        confirm.Click += (_, _) => RecycleRowNote(id);
        danger.Items.Add(confirm);
        menu.Items.Add(danger);
        menu.ShowAt(row, args.GetPosition(row));
    }

    void RowTogglePin(Guid id)
    {
        if (!FlushIfEditing(id)) { Status.Text = Strings.Notes_SaveFailed; return; }
        try { togglePin(id); }
        catch (InvalidOperationException error) { Status.Text = error.Message; }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Notes_LayoutSaveFailed; }
        RefreshPin();
        ReloadList();
    }

    MenuFlyoutSubItem RowReminderMenu(Guid id)
    {
        var menu = new MenuFlyoutSubItem { Text = Strings.Notes_Reminder };
        void AddPreset(string text, Func<DateTimeOffset> due)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                // 行菜单改任意便签的提醒；正编辑同一条时先保草稿，避免提醒和未保存内容脱节。
                if (!FlushIfEditing(id)) { Status.Text = Strings.Notes_SaveFailed; return; }
                if (store.Find(id) == null) return;
                var time = due();
                setReminder(id, time);
                Status.Text = string.Format(Strings.Notes_ReminderAt, time.ToLocalTime());
            };
            menu.Items.Add(item);
        }
        AddPreset(Strings.Notes_ReminderTenMinutes, () => DateTimeOffset.UtcNow.AddMinutes(10));
        AddPreset(Strings.Notes_ReminderFifteenMinutes, () => DateTimeOffset.UtcNow.AddMinutes(15));
        AddPreset(Strings.Notes_ReminderThirtyMinutes, () => DateTimeOffset.UtcNow.AddMinutes(30));
        AddPreset(Strings.Notes_ReminderOneHour, () => DateTimeOffset.UtcNow.AddHours(1));
        AddPreset(Strings.Notes_ReminderThreeHours, () => DateTimeOffset.UtcNow.AddHours(3));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddPreset(Strings.Notes_ReminderNextEvening, NextReminderEvening);
        AddPreset(Strings.Notes_ReminderTomorrowMorning, TomorrowReminderMorning);
        var cancel = new MenuFlyoutItem { Text = Strings.Notes_ReminderCancel };
        cancel.Click += (_, _) =>
        {
            if (!FlushIfEditing(id)) { Status.Text = Strings.Notes_SaveFailed; return; }
            setReminder(id, null);
            Status.Text = Strings.Notes_ReminderCancelled;
        };
        menu.Items.Add(cancel);
        return menu;
    }

    void OpenRowHistory(Guid id)
    {
        if (!FlushIfEditing(id) || store.Find(id) == null) return;
        try { Switch(sessions.Open(id)); }
        catch (KeyNotFoundException) { return; }
        PresentSession();
        HistoryClicked(this, new RoutedEventArgs());
    }

    async void ExportRowFile(Guid id) => await RunFileOperation(async token =>
    {
        if (!FlushIfEditing(id)) return;
        await ExportBody(id, token);
    }, Strings.Notes_ExportFileFailed);

    /// <summary>正编辑该便签时先结清草稿（不切换选中项）。未落盘的会话拒绝操作。</summary>
    bool FlushIfEditing(Guid id) => sessions.Find(id) is not { } session ? true
        : session.Persisted ? sessions.Flush(session)
        : false;

    void RecycleRowNote(Guid id)
    {
        // 正编辑的便签走编辑器删除路径（连同撤销提示一起处理并换选）。
        if (current?.Id == id)
        {
            DeleteClicked(this, new RoutedEventArgs());
            return;
        }
        if (!FlushIfEditing(id)) { Status.Text = Strings.Notes_SaveFailed; return; }
        var snapshot = captureDelete(id);
        if (snapshot == null) return;
        recycleNote(id);
        sessions.Forget(id);
        ReloadTags();
        ReloadList();
        ShowUndo(snapshot);
        Status.Text = Strings.Notes_Recycled;
    }
}
