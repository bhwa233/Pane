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

/// <summary>整条便签提醒（notes-mvp §5）：管理器、桌面窗口和列表标记读同一条提醒，
/// 新增或取消只走同一入口。无任务级提醒。</summary>
public sealed partial class NotesView
{
    const int MaximumReminderMinutes = 365 * 24 * 60;
    Guid? customReminderNote;

    void ShowCustomReminder(Guid note, Button anchor)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!CanChangeReminder(note)) return;
            customReminderNote = note;
            CustomReminderMinutes.Text = "";
            CustomReminderStatus.Visibility = Visibility.Collapsed;
            CustomReminderFlyout.ShowAt(anchor);
            CustomReminderMinutes.Focus(FocusState.Programmatic);
        });
    }

    void CustomReminderClosed(object sender, object args) => customReminderNote = null;

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
        if (customReminderNote is not { } id || !CanChangeReminder(id))
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
        if (ChangeReminder(DateTimeOffset.UtcNow.AddMinutes(minutes))) CustomReminderFlyout.Hide();
        else
        {
            CustomReminderStatus.Text = Strings.Settings_SaveFailed;
            CustomReminderStatus.Visibility = Visibility.Visible;
        }
    }

    bool CanChangeReminder(Guid note) => ready && !disposed && !ViewingRecycle && historyNoteId is null &&
        current is { Persisted: true } session && session.Id == note;

    void RefreshReminderBadge(ListViewItem row, Guid id)
    {
        var due = getReminder(id);
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

    void ReminderMenuOpening(object sender, object args)
    {
        if (sender is not MenuFlyout menu) return;
        if (current is { } session) BuildReminderMenu(menu, session.Id, ReminderButton);
        else menu.Items.Clear();
    }

    void BuildReminderMenu(MenuFlyout menu, Guid note, Button anchor)
    {
        menu.Items.Clear();
        if (!CanChangeReminder(note)) return;
        var current = getReminder(note);
        if (current is { } time)
        {
            menu.Items.Add(new MenuFlyoutItem { IsEnabled = false, Text = string.Format(Strings.Notes_ReminderAt, time.ToLocalTime()) });
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        void AddPreset(string text, Func<DateTimeOffset> due) => menu.Items.Add(ReminderItem(text, () =>
        {
            if (CanChangeReminder(note)) ChangeReminder(due());
        }));

        AddPreset(Strings.Notes_ReminderTenMinutes, () => DateTimeOffset.UtcNow.AddMinutes(10));
        AddPreset(Strings.Notes_ReminderFifteenMinutes, () => DateTimeOffset.UtcNow.AddMinutes(15));
        AddPreset(Strings.Notes_ReminderThirtyMinutes, () => DateTimeOffset.UtcNow.AddMinutes(30));
        AddPreset(Strings.Notes_ReminderOneHour, () => DateTimeOffset.UtcNow.AddHours(1));
        AddPreset(Strings.Notes_ReminderThreeHours, () => DateTimeOffset.UtcNow.AddHours(3));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddPreset(Strings.Notes_ReminderNextEvening, NextReminderEvening);
        AddPreset(Strings.Notes_ReminderTomorrowMorning, TomorrowReminderMorning);
        menu.Items.Add(ReminderItem(Strings.Notes_ReminderCustom, () => ShowCustomReminder(note, anchor)));
        var cancel = ReminderItem(Strings.Notes_ReminderCancel, () =>
        {
            if (CanChangeReminder(note)) ChangeReminder(null);
        });
        cancel.IsEnabled = current.HasValue;
        menu.Items.Add(cancel);
    }

    static MenuFlyoutItem ReminderItem(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }
}
