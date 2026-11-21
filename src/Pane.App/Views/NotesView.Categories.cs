using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Views.Controls;
using Windows.System;

namespace Pane.Views;

// 便签列表的分组、折叠、分类菜单和行菜单（notes-mvp §3）。分类由便签派生（无独立存储），
// 改名/删除是元数据级批量操作；行上显示「已贴到桌面」标记和提醒角标。编辑当前项期间不重排序。
public sealed partial class NotesView
{
    readonly Dictionary<string, bool> collapsedCategories = new(StringComparer.OrdinalIgnoreCase);
    string? categoryPromptRename;
    Guid? categoryPromptMoveNote;

    void ReloadList()
    {
        var query = SearchBox.Text.Trim();
        var category = SelectedCategory;
        updating = true;
        try
        {
            NoteList.Items.Clear();
            var matches = store.Documents.Where(note =>
                (category == null || string.Equals(category, note.Category, StringComparison.OrdinalIgnoreCase)) &&
                (query.Length == 0 || note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                 note.Body.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
            // 分组：未分类排最后，其余按现有筛选下拉的文化序；搜索时忽略折叠（结果全展开）。
            var groups = matches.GroupBy(note => note.Category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key.Length == 0 ? 1 : 0)
                .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);
            foreach (var group in groups)
            {
                NoteList.Items.Add(CategoryHeaderRow(group.Key, group.Count()));
                if (query.Length == 0 && collapsedCategories.GetValueOrDefault(group.Key)) continue;
                foreach (var note in group)
                {
                    var row = NoteRow(note, note.Id);
                    RefreshReminderBadge(row, note.Id);
                    NoteList.Items.Add(row);
                    if (current?.Id == note.Id) NoteList.SelectedItem = row;
                }
            }
            EmptyList.Text = matches.Length > 0 ? "" : query.Length == 0 && category == null ? Strings.Notes_Empty : Strings.Notes_NoResults;
        }
        finally { updating = false; }
    }

    ListViewItem NoteRow(NoteDocument note, object tag)
    {
        var title = string.IsNullOrWhiteSpace(note.Title) ? Strings.Notes_Untitled : note.Title;
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(NoteRowTitle(title, isPinned(note.Id)));
        // 搜索摘要：标题 + 正文拼在一起找首个命中，取命中处前 40 后 60 字的窗口做第二行。
        var query = SearchBox.Text.Trim();
        if (query.Length > 0)
        {
            var haystack = note.Title + "\n" + note.Body;
            var at = haystack.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (at >= 0)
            {
                var start = Math.Max(0, at - 40);
                var window = haystack[start..Math.Min(haystack.Length, at + query.Length + 60)];
                var snippet = new TextBlock
                {
                    FontFamily = BodyBox.FontFamily,
                    FontSize = Status.FontSize,
                    Foreground = EmptyList.Foreground,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, IsHitTestVisible = false,
                };
                MatchText.SetSource(snippet,
                    new MatchedText(window, [new Pane.Contracts.TextSpan(at - start, query.Length)]));
                content.Children.Add(snippet);
            }
        }
        var row = new ListViewItem { Tag = tag, Content = RowShell(content) };
        row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(row, title);
        row.RightTapped += (sender, args) => { if (sender is ListViewItem item) ShowNoteMenu(item, note.Id, args); };
        return row;
    }

    /// <summary>行壳：内容列 + 提醒角标列。RefreshReminderBadge 只动角标列，不再重建内容。</summary>
    static Grid RowShell(object content)
    {
        var shell = new Grid { ColumnSpacing = 6 };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (content is FrameworkElement element) shell.Children.Add(element);
        shell.MinHeight = 32;
        return shell;
    }

    /// <summary>标题行：已贴到桌面时带 pin 标记（notes-mvp §3「已贴到桌面」标签）。</summary>
    StackPanel NoteRowTitle(string title, bool pinned)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (pinned)
        {
            line.Children.Add(new PaneIcon { Glyph = "pin", Width = 11, Height = 11,
                VerticalAlignment = VerticalAlignment.Center, Foreground = EmptyList.Foreground, IsHitTestVisible = false });
        }
        line.Children.Add(new TextBlock
        {
            Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center,
        });
        return line;
    }

    static TextBlock? FindRowTitle(object content) =>
        content is StackPanel panel
            ? panel.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault()
              ?? panel.Children.OfType<TextBlock>().FirstOrDefault()
            : null;

    ListViewItem CategoryHeaderRow(string category, int count)
    {
        var label = category.Length == 0 ? Strings.Notes_Uncategorized : category;
        var row = new ListViewItem { Tag = category };
        row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var content = new Grid { ColumnSpacing = 6 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chevron = new PaneIcon { Glyph = "chev-d", Width = 12, Height = 12,
            VerticalAlignment = VerticalAlignment.Center, Foreground = EmptyList.Foreground, IsHitTestVisible = false };
        if (collapsedCategories.GetValueOrDefault(category)) chevron.RenderTransform = new RotateTransform { Angle = -90 };
        content.Children.Add(chevron);
        var name = new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        content.Children.Add(name);
        var total = new TextBlock { Text = count.ToString(), VerticalAlignment = VerticalAlignment.Center,
            FontSize = SharedState.GroupCountFontSize };
        Grid.SetColumn(total, 2);
        content.Children.Add(total);
        row.Content = content;
        var groupText = string.Format(Strings.Notes_NoteGroupCount, (object?)count);
        AutomationProperties.SetName(row, label);
        ToolTipService.SetToolTip(row, groupText);
        row.Tapped += (sender, args) =>
        {
            args.Handled = true;
            ToggleCategoryCollapse(category);
        };
        if (category.Length != 0)
            row.RightTapped += (sender, args) => { if (sender is ListViewItem item) ShowCategoryMenu(item, category, args); };
        return row;
    }

    void ToggleCategoryCollapse(string category)
    {
        if (collapsedCategories.TryGetValue(category, out var value)) collapsedCategories[category] = !value;
        else collapsedCategories[category] = true;
        // 点的是表头：把因点击产生的选中清掉，避免表头带着选中态。
        updating = true;
        NoteList.SelectedItem = null;
        updating = false;
        ReloadList();
    }

    // ---------------- 分类菜单（表头右键） ----------------

    void ShowCategoryMenu(ListViewItem row, string category, RightTappedRoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = Strings.Notes_CategoryRename };
        rename.Click += (_, _) => OpenCategoryPrompt(null, category, row);
        menu.Items.Add(rename);
        var remove = new MenuFlyoutSubItem { Text = Strings.Notes_CategoryDelete };
        var confirm = new MenuFlyoutItem { Text = Strings.Notes_CategoryDeleteConfirm };
        confirm.Click += (_, _) => DeleteCategory(category);
        remove.Items.Add(confirm);
        menu.Items.Add(remove);
        menu.ShowAt(row, args.GetPosition(row));
    }

    void DeleteCategory(string category)
    {
        try
        {
            var affected = store.ClearCategory(category);
            collapsedCategories.Remove(category);
            if (current is { Category.Length: > 0 } session && string.Equals(session.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                updating = true;
                try { if (sessions.Find(session.Id) is { } live) live.Category = "";
                    CategoryBox.Text = ""; }
                finally { updating = false; }
            }
            ReloadCategories();
            ReloadList();
            Status.Text = string.Format(Strings.Notes_CategoryUpdatedCount, (object?)affected);
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Settings_SaveFailed; }
    }

    // ---------------- 便签行菜单（右键） ----------------

    void ShowNoteMenu(ListViewItem row, Guid id, RightTappedRoutedEventArgs args)
    {
        var menu = new MenuFlyout();
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
        menu.Items.Add(RowMoveMenu(id, row));
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

    MenuFlyoutSubItem RowMoveMenu(Guid id, ListViewItem row)
    {
        var menu = new MenuFlyoutSubItem { Text = Strings.Notes_MoveToCategory };
        var current = store.Find(id)?.Category ?? "";
        foreach (var category in store.Documents.Select(note => note.Category).Where(category => category.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            if (string.Equals(category, current, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new MenuFlyoutItem { Text = category };
            item.Click += (_, _) => RowMove(id, category);
            menu.Items.Add(item);
        }
        if (current.Length != 0)
        {
            var uncategorized = new MenuFlyoutItem { Text = Strings.Notes_Uncategorized };
            uncategorized.Click += (_, _) => RowMove(id, "");
            menu.Items.Add(uncategorized);
        }
        var create = new MenuFlyoutItem { Text = Strings.Notes_NewCategoryAction };
        create.Click += (sender, _) =>
        {
            if (sender is FrameworkElement anchor) OpenCategoryPrompt(id, null, anchor);
        };
        menu.Items.Add(create);
        return menu;
    }

    void RowMove(Guid id, string category)
    {
        if (!FlushIfEditing(id)) { Status.Text = Strings.Notes_SaveFailed; return; }
        try
        {
            if (!store.MoveCategory(id, category)) return;
            if (sessions.Find(id) is { } live) live.Category = category;
            if (current?.Id == id)
            {
                updating = true;
                try { CategoryBox.Text = category; }
                finally { updating = false; }
            }
            ReloadCategories();
            ReloadList();
            Status.Text = string.Format(Strings.Notes_CategoryUpdatedCount, 1);
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Settings_SaveFailed; }
    }

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
        ReloadCategories();
        ReloadList();
        ShowUndo(snapshot);
        Status.Text = Strings.Notes_Recycled;
    }

    // ---------------- 分类改名 / 新建分类（共用输入浮层） ----------------

    void OpenCategoryPrompt(Guid? moveNote, string? rename, FrameworkElement anchor)
    {
        categoryPromptMoveNote = moveNote;
        categoryPromptRename = rename;
        CategoryPromptTitle.Text = rename is null ? Strings.Notes_NewCategoryAction : Strings.Notes_CategoryRenameTitle;
        CategoryPromptInput.Text = rename ?? "";
        CategoryPromptStatus.Visibility = Visibility.Collapsed;
        CategoryPromptFlyout.ShowAt(anchor);
        CategoryPromptInput.Focus(FocusState.Programmatic);
    }

    void CategoryPromptClosed(object sender, object args)
    {
        categoryPromptRename = null;
        categoryPromptMoveNote = null;
    }

    void CancelCategoryPrompt(object sender, RoutedEventArgs args) => CategoryPromptFlyout.Hide();

    void CategoryPromptKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        SaveCategoryPrompt(this, args);
    }

    void SaveCategoryPrompt(object sender, RoutedEventArgs args)
    {
        var name = CategoryPromptInput.Text.Trim();
        if (name.Length == 0)
        {
            CategoryPromptStatus.Text = Strings.Notes_CategoryNameRequired;
            CategoryPromptStatus.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            if (categoryPromptRename is { } old)
            {
                var affected = store.RenameCategory(old, name);
                if (collapsedCategories.TryGetValue(old, out var wasCollapsed) && !string.Equals(old, name, StringComparison.OrdinalIgnoreCase))
                {
                    collapsedCategories.Remove(old);
                    collapsedCategories[name] = wasCollapsed;
                }
                foreach (var live in sessions.List())
                    if (string.Equals(live.Category, old, StringComparison.OrdinalIgnoreCase)) live.Category = name;
                if (current is { } session && string.Equals(session.Category, old, StringComparison.OrdinalIgnoreCase))
                {
                    updating = true;
                    try { CategoryBox.Text = session.Category; }
                    finally { updating = false; }
                }
                ReloadCategories();
                ReloadList();
                Status.Text = string.Format(Strings.Notes_CategoryUpdatedCount, (object?)affected);
            }
            else if (categoryPromptMoveNote is { } note)
            {
                RowMove(note, name);
            }
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Settings_SaveFailed; return; }
        catch (ArgumentException) { Status.Text = Strings.Notes_CategoryNameRequired; return; }
        CategoryPromptFlyout.Hide();
    }

    // 表头行字号用一个小一点的静态值，避免每次分配。
    static class SharedState
    {
        public const double GroupCountFontSize = 11;
    }
}
