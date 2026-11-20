using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Contracts;
using Pane.Localization;
using Pane.Services.Launcher;
using Pane.Settings;
using Windows.System;

namespace Pane.Views;

public sealed partial class LauncherView
{
    LauncherItem? quickLinkItem;
    readonly Dictionary<string, TextBox> quickLinkFields = new(StringComparer.Ordinal);

    bool TryShowQuickLinkForm(LauncherItem item)
    {
        if (item.Group != LauncherGroups.QuickLinks || search is not LauncherSearch launcher ||
            launcher.GetQuickLinkPrompt(item) is not { } prompt) return false;
        if (quickLinkItem is not null) return true;

        pending?.Cancel();
        Disarm();
        ShowNumbers(false);
        quickLinkItem = item;
        QuickLinkFormTitle.Text = item.Title;
        QuickLinkFormScroll.MaxHeight = Math.Min(220, ResultsMaxHeight - 102);
        foreach (var field in prompt.Fields)
        {
            var label = field == QuickLinkRules.QueryField ? Strings.QuickLinks_QueryField : field;
            var input = new TextBox
            {
                Header = label,
                MaxLength = QuickLinkRules.QueryLimit,
                Text = field == QuickLinkRules.QueryField ? prompt.Query : "",
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
            };
            AutomationProperties.SetName(input, label);
            QuickLinkFields.Children.Add(input);
            quickLinkFields.Add(field, input);
        }

        Input.IsReadOnly = true;
        Input.IsTabStop = false;
        Scroller.Visibility = Foot.Visibility = Visibility.Collapsed;
        ResultsArea.Visibility = QuickLinkFormPanel.Visibility = Visibility.Visible;
        RequestHeight();
        QuickLinkFormScroll.ChangeView(null, 0, null, true);
        (quickLinkFields.Values.FirstOrDefault(field => field.Text.Length == 0) ?? quickLinkFields.Values.First()).Focus(FocusState.Programmatic);
        return true;
    }

    void ResetQuickLinkForm()
    {
        if (quickLinkItem is null) return;
        quickLinkItem = null;
        quickLinkFields.Clear();
        QuickLinkFields.Children.Clear();
        QuickLinkFormError.Text = "";
        QuickLinkFormError.Visibility = QuickLinkFormPanel.Visibility = Visibility.Collapsed;
        QuickLinkOpen.IsEnabled = true;
        Input.IsTabStop = true;
        Input.IsReadOnly = false;
        Scroller.Visibility = Visibility.Visible;
        Foot.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnQuickLinkCancel(object sender, RoutedEventArgs e) => CancelQuickLinkForm();

    void CancelQuickLinkForm()
    {
        ResetQuickLinkForm();
        Input.Focus(FocusState.Programmatic);
        RequestHeight();
        _ = Refresh(Input.Text);
    }

    void OnQuickLinkFormKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (quickLinkItem is null) return;
        if (e.Key == VirtualKey.Escape) { e.Handled = true; CancelQuickLinkForm(); }
    }

    async void OnQuickLinkOpen(object sender, RoutedEventArgs e)
    {
        if (quickLinkItem is not { } item || search is not LauncherSearch launcher || !QuickLinkOpen.IsEnabled) return;
        if (quickLinkFields.Values.FirstOrDefault(field => string.IsNullOrWhiteSpace(field.Text)) is { } missing)
        {
            ShowQuickLinkError(Strings.QuickLinks_FieldsRequired);
            missing.Focus(FocusState.Programmatic);
            return;
        }

        QuickLinkOpen.IsEnabled = false;
        try
        {
            var values = quickLinkFields.ToDictionary(field => field.Key, field => field.Value.Text, StringComparer.Ordinal);
            await launcher.ExecuteQuickLinkAsync(item, values, CancellationToken.None);
            if (ReferenceEquals(quickLinkItem, item)) CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(quickLinkItem, item) && (error is LauncherActionException ? error.Message : App.FailureReason(error)) is { } reason)
                ShowQuickLinkError(reason);
        }
        finally { if (ReferenceEquals(quickLinkItem, item)) QuickLinkOpen.IsEnabled = true; }
    }

    void ShowQuickLinkError(string reason)
    {
        QuickLinkFormError.Text = reason;
        QuickLinkFormError.Visibility = Visibility.Visible;
        RequestHeight();
    }
}
