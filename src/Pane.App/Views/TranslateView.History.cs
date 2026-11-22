using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Localization;
using Pane.Services.Ai;
using Pane.Settings;
using Windows.System;

namespace Pane.Views;

public sealed partial class TranslateView
{
    readonly TranslationHistory history;
    IReadOnlyList<TranslationHistoryEntry> historyEntries = [];
    bool HistoryVisible => HistoryPanel.Visibility == Visibility.Visible;
    TranslationHistoryEntry? SelectedHistory => HistoryList.SelectedIndex is var index && index >= 0 && index < historyEntries.Count
        ? historyEntries[index] : null;

    void RecordHistory(TranslationHistoryEntry entry)
    {
        try { if (!history.Record(entry)) StateText.Text = Strings.Translate_HistorySaveFailed; }
        catch (Exception error) when (TranslationHistory.IsStorageError(error)) { StateText.Text = Strings.Translate_HistorySaveFailed; }
    }

    void OnToggleHistory(object sender, RoutedEventArgs args) => ShowHistory(!HistoryVisible);

    void ShowHistory(bool visible)
    {
        if (visible)
        {
            if (running != null) { last = ""; StateText.Text = ""; }
            CancelPending();
        }
        HistoryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Workspace.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        LanguagesBar.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        Hints.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        HistoryIcon.IsFilled = visible;
        var label = visible ? Strings.Translate_CloseHistory : Strings.Translate_History;
        AutomationProperties.SetName(HistoryButton, label);
        ToolTipService.SetToolTip(HistoryButton, label);
        if (!visible) return;
        RefreshHistory();
        if (historyEntries.Count > 0) HistoryList.Focus(FocusState.Programmatic);
    }

    void RefreshHistory()
    {
        HistoryTitle.Text = Strings.Translate_TranslationHistory;
        AutomationProperties.SetName(HistoryList, Strings.Translate_TranslationHistory);
        RememberHistory.IsChecked = settings.Current.RememberTranslations;
        HistoryStatus.Text = "";
        try { historyEntries = history.Read(); }
        catch (Exception error) when (TranslationHistory.IsStorageError(error))
        {
            historyEntries = [];
            HistoryStatus.Text = Strings.Translate_HistoryReadFailed;
        }
        HistoryList.ItemsSource = historyEntries.Select(entry =>
        {
            var line = entry.Source.Replace('\n', ' ').Replace('\r', ' ');
            if (line.Length > 64) line = line[..(char.IsHighSurrogate(line[63]) ? 63 : 64)] + "…";
            return $"{line}  ·  {NameOf(entry.Target!)}";
        }).ToArray();
        HistoryEmpty.Visibility = historyEntries.Count == 0 && HistoryStatus.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = historyEntries.Count != 0;
        UpdateHistoryActions();
    }

    void OnHistorySelection(object sender, SelectionChangedEventArgs args) => UpdateHistoryActions();

    void UpdateHistoryActions()
    {
        OpenHistoryItemButton.IsEnabled = DeleteHistoryButton.IsEnabled = SelectedHistory != null;
    }

    void OnRememberHistory(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox control || control.IsChecked is not { } enabled ||
            settings.Current.RememberTranslations == enabled || !HistoryVisible) return;
        try
        {
            settings.Update(value => value with { RememberTranslations = enabled });
            RefreshHistory();
        }
        catch (Exception error) when (TranslationHistory.IsStorageError(error) || error is ArgumentException)
        {
            RememberHistory.IsChecked = settings.Current.RememberTranslations;
            HistoryStatus.Text = Strings.Settings_SaveFailed;
        }
    }

    void OnHistoryListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || SelectedHistory == null) return;
        args.Handled = true;
        OpenHistoryEntry();
    }

    void OnOpenHistoryItem(object sender, RoutedEventArgs args) => OpenHistoryEntry();

    void OpenHistoryEntry()
    {
        if (SelectedHistory is not { } entry) return;
        ShowHistory(false);
        SetSource(entry.Source);
        // 恢复历史条目不重新请求：回填保存的目标与译文，正文时间不推进。
        manualTarget = entry.Target;
        last = entry.Source;
        ResetLanguages(entry.Source);
        Out.Foreground = TextProto.Foreground;
        Out.Text = entry.Result!;
        OutScroller.ChangeView(null, 0, null, true);
        Source.Focus(FocusState.Programmatic);
    }

    void OnDeleteHistory(object sender, RoutedEventArgs args)
    {
        if (SelectedHistory is not { } entry) return;
        ChangeHistory(() => history.Remove(entry));
    }

    void OnClearHistory(object sender, RoutedEventArgs args) => ChangeHistory(() => history.Clear());

    void ChangeHistory(Action change)
    {
        try { change(); RefreshHistory(); }
        catch (Exception error) when (TranslationHistory.IsStorageError(error)) { HistoryStatus.Text = Strings.Translate_HistorySaveFailed; }
    }
}
