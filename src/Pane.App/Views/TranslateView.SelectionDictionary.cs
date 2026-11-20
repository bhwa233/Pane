using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Localization;
using Pane.Services.Ai;

namespace Pane.Views;

public sealed partial class TranslateView
{
    readonly DispatcherQueueTimerHolder selectionLookupDebounce;
    CancellationTokenSource? selectionLookup;
    FrameworkElement? selectionLookupOwner;
    string selectionLookupQuery = "";

    void RefreshSelectionDictionaryVisibility()
    {
        bool translating = !IsDictionary && !HistoryVisible;
        SelectionDictionaryToggle.Visibility = translating ? Visibility.Visible : Visibility.Collapsed;
        SelectionDictionaryPanel.Visibility = translating && SelectionDictionaryToggle.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnToggleSelectionDictionary(object sender, RoutedEventArgs args)
    {
        if (mode is null || sender is not CheckBox { IsChecked: bool enabled } toggle) return;
        if (historySettings.Current.LookupTranslationSelection != enabled)
        {
            try { historySettings.Update(settings => settings with { LookupTranslationSelection = enabled }); }
            catch (Exception error) when (TranslationHistory.IsStorageError(error) || error is ArgumentException)
            {
                toggle.IsChecked = historySettings.Current.LookupTranslationSelection;
                StateText.Text = Strings.Settings_SaveFailed;
                return;
            }
        }
        ClearSelectionLookup();
        RefreshSelectionDictionaryVisibility();
    }

    void OnSourceSelectionChanged(object sender, RoutedEventArgs args)
    {
        if (mode != null && !settingSource) QueueSelectionLookup(Source, Source.SelectedText);
    }

    void QueueSelectionLookup(FrameworkElement owner, string? selected)
    {
        if (SelectionDictionaryPanel.Visibility != Visibility.Visible) return;
        var query = selected?.Trim() ?? "";
        if (query.Length == 0)
        {
            if (selectionLookupOwner == owner) StopSelectionLookup();
            return;
        }
        if (selectionLookupOwner == owner && selectionLookupQuery == query) return;
        ClearSelectionLookup();
        if (query.Length > 512) return;
        selectionLookupOwner = owner;
        selectionLookupQuery = query;
        selectionLookupDebounce.Restart();
    }

    void StopSelectionLookup()
    {
        selectionLookupDebounce.Stop();
        selectionLookupQuery = "";
        var pending = selectionLookup;
        selectionLookup = null;
        pending?.Cancel();
    }

    void ClearSelectionLookup()
    {
        StopSelectionLookup();
        selectionLookupOwner = null;
        SelectionDictionaryText.Text = "";
    }

    async Task LookupSelectionAsync()
    {
        var query = selectionLookupQuery;
        if (query.Length == 0 || SelectionDictionaryPanel.Visibility != Visibility.Visible) return;
        using var lookup = selectionLookup = new CancellationTokenSource();
        var token = lookup.Token;
        try
        {
            var entries = await Task.Run(() =>
            {
                var path = OfflineDictionary.FindPath(historySettings.DirectoryPath, AppContext.BaseDirectory);
                return path == null ? null : OfflineDictionary.Lookup(path, query, token);
            }, token);
            if (lookup != selectionLookup) return;
            SelectionDictionaryText.Text = entries switch
            {
                null => Strings.Translate_DictionaryUnavailable,
                { Count: 0 } => Strings.Translate_DictionaryNoMatch,
                _ => FormatDictionaryEntries(entries)
            };
            SelectionDictionaryScroll.ChangeView(null, 0, null, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (lookup == selectionLookup) SelectionDictionaryText.Text = Strings.Translate_DictionaryUnavailable;
        }
        finally { if (lookup == selectionLookup) selectionLookup = null; }
    }

    static string FormatDictionaryEntries(IReadOnlyList<DictionaryEntry> entries) =>
        string.Join("\n\n", entries.Select(entry => string.IsNullOrEmpty(entry.Phonetic)
            ? $"{entry.Word}\n{entry.Translation}"
            : $"{entry.Word}  /{entry.Phonetic}/\n{entry.Translation}"));
}
