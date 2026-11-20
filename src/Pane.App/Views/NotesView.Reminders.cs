using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Localization;
using Pane.Settings;
using Pane.Views.Controls;
using Windows.System;

namespace Pane.Views;

public sealed partial class NotesView
{
    const int MaximumReminderMinutes = 365 * 24 * 60;
    Guid? customReminderNote;
    string? customReminderTask;

    void ShowCustomReminder(Guid note, string? task, Button anchor)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!CanChangeReminder(note) || (task is not null && !previewing)) return;
            customReminderNote = note;
            customReminderTask = task;
            CustomReminderScope.Text = task ?? "";
            CustomReminderScope.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
            CustomReminderMinutes.Text = "";
            CustomReminderStatus.Visibility = Visibility.Collapsed;
            CustomReminderFlyout.ShowAt(anchor);
            CustomReminderMinutes.Focus(FocusState.Programmatic);
        });
    }

    void CustomReminderClosed(object sender, object args)
    {
        customReminderNote = null;
        customReminderTask = null;
    }

    void CancelCustomReminder(object sender, RoutedEventArgs args) => CustomReminderFlyout.Hide();

    void CustomReminderKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        ApplyCustomReminder();
    }

    void SaveCustomReminder(object sender, RoutedEventArgs args) => ApplyCustomReminder();

    void ApplyCustomReminder()
    {
        if (customReminderNote is not { } id || !CanChangeReminder(id) || (customReminderTask is not null && !previewing))
        {
            CustomReminderFlyout.Hide();
            return;
        }
        if (!int.TryParse(CustomReminderMinutes.Text, out var minutes) || minutes is < 1 or > MaximumReminderMinutes)
        {
            CustomReminderStatus.Text = string.Format(Strings.Notes_ReminderInvalidMinutes, MaximumReminderMinutes);
            CustomReminderStatus.Visibility = Visibility.Visible;
            CustomReminderMinutes.Focus(FocusState.Programmatic);
            return;
        }
        if (ChangeReminder(DateTimeOffset.UtcNow.AddMinutes(minutes), customReminderTask)) CustomReminderFlyout.Hide();
        else
        {
            CustomReminderStatus.Text = Strings.Settings_SaveFailed;
            CustomReminderStatus.Visibility = Visibility.Visible;
        }
    }

    bool CanChangeReminder(Guid note) => ready && !disposed && !ViewingRecycle && historyNoteId is null &&
        store.DraftIsSaved && store.Draft?.Id == note;

    void RefreshReminderBadge(ListViewItem row, Guid id)
    {
        var due = getReminder(id);
        if (getTaskReminders(id) is { } tasks)
            foreach (var (task, time) in tasks)
                if (task.Length is > 0 and <= NoteReminderSchedule.TaskLabelLimit && (due is null || time < due.Value)) due = time;

        var label = due is { } next ? string.Format(Strings.Notes_ReminderAt, next.ToLocalTime()) : "";
        if (label == (AutomationProperties.GetHelpText(row) ?? "")) return;
        AutomationProperties.SetHelpText(row, label);
        ToolTipService.SetToolTip(row, due.HasValue ? label : null);
        // 行壳（RowShell）：最后一列是角标槽，只增删槽里的图标，不重建行内容（摘要在里面）。
        if (row.Content is not Grid shell || shell.ColumnDefinitions.Count == 0) return;
        var slot = shell.ColumnDefinitions.Count - 1;
        for (var index = shell.Children.Count - 1; index >= 0; index--)
            if (shell.Children[index] is PaneIcon icon && Grid.GetColumn(icon) == slot) shell.Children.RemoveAt(index);
        if (due.HasValue)
        {
            var icon = new PaneIcon { Glyph = "timer", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center,
                Foreground = EmptyList.Foreground, IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            Grid.SetColumn(icon, slot);
            shell.Children.Add(icon);
        }
    }

    void AddTaskReminderButton(Grid row, Guid note, string task)
    {
        if (task.Length is 0 or > NoteReminderSchedule.TaskLabelLimit || !store.DraftIsSaved) return;
        var anchor = new Button
        {
            Style = ReminderButton.Style, Width = 28, Height = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new PaneIcon { Glyph = "timer", Width = 14, Height = 14 },
        };
        var label = string.Format(Strings.Notes_TaskReminderFor, task);
        AutomationProperties.SetName(anchor, label);
        ToolTipService.SetToolTip(anchor, Strings.Notes_TaskReminder);

        var menu = new MenuFlyout();
        menu.Opening += (_, _) => BuildReminderMenu(menu, note, task, anchor);
        anchor.Flyout = menu;
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(anchor, 2);
        row.Children.Add(anchor);
    }

    void ReminderMenuOpening(object sender, object args)
    {
        if (sender is not MenuFlyout menu) return;
        if (store.Draft is { } draft) BuildReminderMenu(menu, draft.Id, null, ReminderButton);
        else menu.Items.Clear();
    }

    void BuildReminderMenu(MenuFlyout menu, Guid note, string? task, Button anchor)
    {
        menu.Items.Clear();
        if (!CanChangeReminder(note) || (task is not null && !previewing)) return;
        var current = task is null ? getReminder(note) :
            getTaskReminders(note)?.TryGetValue(task, out var taskTime) == true ? taskTime : (DateTimeOffset?)null;
        if (current is { } time)
        {
            menu.Items.Add(new MenuFlyoutItem { IsEnabled = false, Text = string.Format(Strings.Notes_ReminderAt, time.ToLocalTime()) });
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        void AddPreset(string text, Func<DateTimeOffset> due) => menu.Items.Add(ReminderItem(text, () =>
        {
            if (CanChangeReminder(note) && (task is null || previewing)) ChangeReminder(due(), task);
        }));

        AddPreset(Strings.Notes_ReminderTenMinutes, () => DateTimeOffset.UtcNow.AddMinutes(10));
        AddPreset(Strings.Notes_ReminderFifteenMinutes, () => DateTimeOffset.UtcNow.AddMinutes(15));
        AddPreset(Strings.Notes_ReminderThirtyMinutes, () => DateTimeOffset.UtcNow.AddMinutes(30));
        AddPreset(Strings.Notes_ReminderOneHour, () => DateTimeOffset.UtcNow.AddHours(1));
        AddPreset(Strings.Notes_ReminderThreeHours, () => DateTimeOffset.UtcNow.AddHours(3));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddPreset(Strings.Notes_ReminderNextEvening, NextReminderEvening);
        AddPreset(Strings.Notes_ReminderTomorrowMorning, TomorrowReminderMorning);
        menu.Items.Add(ReminderItem(Strings.Notes_ReminderCustom, () => ShowCustomReminder(note, task, anchor)));
        var cancel = ReminderItem(Strings.Notes_ReminderCancel, () =>
        {
            if (CanChangeReminder(note) && (task is null || previewing)) ChangeReminder(null, task);
        });
        cancel.IsEnabled = current.HasValue;
        menu.Items.Add(cancel);
        if (task is null && getTaskReminders(note) is { Count: > 0 } tasks) AddScheduledTasks(menu, note, tasks);
    }

    void AddScheduledTasks(MenuFlyout menu, Guid note, IReadOnlyDictionary<string, DateTimeOffset> tasks)
    {
        var scheduled = new MenuFlyoutSubItem { Text = Strings.Notes_TaskReminders };
        foreach (var (task, due) in tasks.OrderBy(entry => entry.Value))
        {
            if (task.Length is 0 or > NoteReminderSchedule.TaskLabelLimit) continue;
            var shortTask = task.Length <= 32 ? task : task[..(char.IsHighSurrogate(task[31]) ? 31 : 32)] + "…";
            var item = new MenuFlyoutSubItem { Text = shortTask };
            AutomationProperties.SetName(item, task);
            item.Items.Add(new MenuFlyoutItem { IsEnabled = false, Text = string.Format(Strings.Notes_ReminderAt, due.ToLocalTime()) });
            item.Items.Add(ReminderItem(Strings.Notes_ReminderCancel, () =>
            {
                if (CanChangeReminder(note)) ChangeReminder(null, task);
            }));
            scheduled.Items.Add(item);
        }
        if (scheduled.Items.Count > 0) menu.Items.Add(scheduled);
    }

    static MenuFlyoutItem ReminderItem(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }
}
