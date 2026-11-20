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
    readonly SettingsStore historySettings;
    readonly TranslationHistory dictionaryHistory, translationHistory;
    IReadOnlyList<TranslationHistoryEntry> historyEntries = [];
    TranslationHistory CurrentHistory => IsDictionary ? dictionaryHistory : translationHistory;
    bool HistoryVisible => HistoryPanel.Visibility == Visibility.Visible;
    TranslationHistoryEntry? SelectedHistory => HistoryList.SelectedIndex is var index && index >= 0 && index < historyEntries.Count
        ? historyEntries[index] : null;

    void RecordHistory(TranslationHistoryEntry entry)
    {
        try { if (!CurrentHistory.Record(entry)) StateText.Text = Strings.Translate_HistorySaveFailed; }
        catch (Exception error) when (TranslationHistory.IsStorageError(error)) { StateText.Text = Strings.Translate_HistorySaveFailed; }
    }

    void OnToggleHistory(object sender, RoutedEventArgs args) => ShowHistory(!HistoryVisible);

    void ShowHistory(bool visible)
    {
        if (visible)
        {
            if (running != null) { last = ""; StateText.Text = ""; }
            CancelPending();
            ClearSelectionLookup();
        }
        HistoryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Workspace.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        LanguagesBar.Visibility = visible || IsDictionary ? Visibility.Collapsed : Visibility.Visible;
        Hints.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        RefreshSelectionDictionaryVisibility();
        HistoryIcon.IsFilled = visible;
        var label = visible ? Strings.Translate_CloseHistory : Strings.Translate_History;
        AutomationProperties.SetName(HistoryButton, label);
        ToolTipService.SetToolTip(HistoryButton, label);
        if (!visible)
        {
            if (IsDictionary && ActionButton.Visibility == Visibility.Visible && messageAction == MessageAction.InstallDictionary)
                _ = Lookup(force: true);
            return;
        }
        RefreshHistory();
        if (historyEntries.Count > 0) HistoryList.Focus(FocusState.Programmatic);
    }

    void RefreshHistory()
    {
        HistoryTitle.Text = IsDictionary ? Strings.Translate_DictionaryHistory : Strings.Translate_TranslationHistory;
        AutomationProperties.SetName(HistoryList, HistoryTitle.Text);
        RememberHistory.Visibility = IsDictionary ? Visibility.Collapsed : Visibility.Visible;
        RememberHistory.IsChecked = historySettings.Current.RememberTranslations;
        HistoryStatus.Text = "";
        try { historyEntries = CurrentHistory.Read(); }
        catch (Exception error) when (TranslationHistory.IsStorageError(error))
        {
            historyEntries = [];
            HistoryStatus.Text = Strings.Translate_HistoryReadFailed;
        }
        HistoryList.ItemsSource = historyEntries.Select(entry =>
        {
            var line = entry.Source.Replace('\n', ' ').Replace('\r', ' ');
            if (line.Length > 64) line = line[..(char.IsHighSurrogate(line[63]) ? 63 : 64)] + "…";
            return entry.Target == null ? line : $"{line}  ·  {NameOf(entry.Target)}";
        }).ToArray();
        HistoryEmpty.Visibility = historyEntries.Count == 0 && HistoryStatus.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = historyEntries.Count != 0;
        UpdateHistoryActions();
    }

    void OnHistorySelection(object sender, SelectionChangedEventArgs args) => UpdateHistoryActions();

    void UpdateHistoryActions()
    {
        if (OpenHistoryItemButton == null) return;
        OpenHistoryItemButton.IsEnabled = DeleteHistoryButton.IsEnabled = SelectedHistory != null;
    }

    void OnRememberHistory(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox control || control.IsChecked is not { } enabled ||
            historySettings.Current.RememberTranslations == enabled || !HistoryVisible || IsDictionary) return;
        try
        {
            historySettings.Update(settings => settings with { RememberTranslations = enabled });
            RefreshHistory();
        }
        catch (Exception error) when (TranslationHistory.IsStorageError(error) || error is ArgumentException)
        {
            RememberHistory.IsChecked = historySettings.Current.RememberTranslations;
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
        if (IsDictionary) _ = Lookup(force: true);
        else
        {
            manualTarget = entry.Target;
            last = entry.Source;
            ResetLanguages(entry.Source);
            ShowIdle();
            Out.Foreground = TextProto.Foreground;
            Out.Text = entry.Result!;
            SaveNoteButton.IsEnabled = !string.IsNullOrWhiteSpace(Out.Text);
            OutScroller.ChangeView(null, 0, null, true);
        }
        Source.Focus(FocusState.Programmatic);
    }

    void OnDeleteHistory(object sender, RoutedEventArgs args)
    {
        if (SelectedHistory is not { } entry) return;
        ChangeHistory(() => CurrentHistory.Remove(entry));
    }

    void OnClearHistory(object sender, RoutedEventArgs args) => ChangeHistory(() => CurrentHistory.Clear());

    void ChangeHistory(Action change)
    {
        try { change(); RefreshHistory(); }
        catch (Exception error) when (TranslationHistory.IsStorageError(error)) { HistoryStatus.Text = Strings.Translate_HistorySaveFailed; }
    }
}
