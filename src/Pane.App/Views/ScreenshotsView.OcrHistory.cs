using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Localization;
using Pane.Services.Screenshots;
using Pane.Views.Controls;

namespace Pane.Views;

public sealed partial class ScreenshotsView
{
    const string ScreenshotMode = "screenshots", OcrHistoryMode = "ocr";
    PaneSegmented screenshotMode = null!;
    bool viewingOcrHistory, loadingOcrHistory, editingOcr;
    OcrImageResult? pendingOcr;
    SavedOcr? savedOcr;

    void InitializeScreenshotMode()
    {
        screenshotMode = new([(ScreenshotMode, Strings.Module_Screenshots_Name), (OcrHistoryMode, Strings.Screenshots_OcrHistory)],
            ScreenshotMode, ModeTextStrong, ModeTextDim, ModeFill.Background, ModeFill.BorderBrush);
        AutomationProperties.SetName(screenshotMode, Strings.Module_Screenshots_Name + " / " + Strings.Screenshots_OcrHistory);
        ScreenshotModeHost.Content = screenshotMode;
        screenshotMode.Changed += (_, mode) =>
        {
            if (mode == OcrHistoryMode)
            {
                viewingOcrHistory = true;
                History.Visibility = ScreenshotTools.Visibility = Visibility.Collapsed;
                OcrHistoryList.Visibility = Visibility.Visible;
                BindOcrHistory();
            }
            else
            {
                ShowScreenshotMode();
                LoadHistory(selected);
            }
        };
    }

    void ShowScreenshotMode()
    {
        viewingOcrHistory = false;
        History.Visibility = ScreenshotTools.Visibility = Visibility.Visible;
        OcrHistoryList.Visibility = Visibility.Collapsed;
    }

    void BindOcrHistory()
    {
        loadingOcrHistory = true;
        try
        {
            OcrHistoryList.ItemsSource = ocrHistory.List().Select(record => new ListViewItem
                { Tag = record.Id, Content = record.CreatedUtc.ToLocalTime().ToString("g") }).ToArray();
            OcrHistoryList.SelectedIndex = -1;
            ShowOcrHistoryEmptyState();
        }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error))
        {
            OcrHistoryList.ItemsSource = null;
            ShowImage();
            Status.Text = Strings.Screenshots_LoadFailed;
            RefreshPin();
        }
        finally { loadingOcrHistory = false; }
    }

    void ShowOcrHistoryEmptyState()
    {
        ShowImage();
        Status.Text = OcrHistoryList.Items.Count == 0 ? Strings.Screenshots_OcrHistoryEmpty : "";
        RefreshPin();
    }

    async void OcrHistorySelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (loadingOcrHistory || !viewingOcrHistory || busy || disposed) return;
        ShowOcrHistoryEmptyState();
        if ((OcrHistoryList.SelectedItem as ListViewItem)?.Tag is not Guid id) return;
        await Run(async token =>
        {
            var (record, bytes) = await Task.Run(() => ocrHistory.Read(id), token);
            if (token.IsCancellationRequested) return;
            var bitmap = await DecodeOcrPreviewAsync(bytes, record.Original);
            if (token.IsCancellationRequested) return;
            savedOcr = record;
            PresentOcr(bitmap, record.Original, record.Text);
        });
    }

    void PresentOcr(BitmapImage bitmap, OcrRecognition geometry, string text)
    {
        ImageActions.Visibility = PreviewFrame.Visibility = Visibility.Collapsed;
        ResetOcrImageSelection();
        ocrGeometry = geometry;
        OcrPreview.Source = bitmap;
        OcrResult.Text = text;
        ocrFitPending = true;
        OcrWorkspace.Visibility = Visibility.Visible;
        LayoutOcrViewport();
        Status.Text = string.IsNullOrWhiteSpace(text) ? Strings.Screenshots_OcrEmpty : "";
        RefreshOcrHistoryActions();
        OcrResult.Focus(FocusState.Programmatic);
    }

    void RefreshOcrHistoryActions()
    {
        var resultVisible = OcrWorkspace.Visibility == Visibility.Visible;
        OcrResultActions.Visibility = resultVisible && !editingOcr ? Visibility.Visible : Visibility.Collapsed;
        OcrEditActions.Visibility = resultVisible && editingOcr ? Visibility.Visible : Visibility.Collapsed;
        SaveOcrButton.Visibility = pendingOcr != null ? Visibility.Visible : Visibility.Collapsed;
        OcrVersionsButton.Visibility = savedOcr is { Corrections.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        DeleteOcrButton.Visibility = savedOcr != null ? Visibility.Visible : Visibility.Collapsed;
        BackToImageButton.IsEnabled = SaveOcrButton.IsEnabled = EditOcrButton.IsEnabled = OcrVersionsButton.IsEnabled =
            DeleteOcrButton.IsEnabled = CancelOcrEditButton.IsEnabled = CommitOcrEditButton.IsEnabled = !busy;
        screenshotMode.IsEnabled = History.IsEnabled = OcrHistoryList.IsEnabled = !busy && !editingOcr;
    }

    void EditOcrClicked(object sender, RoutedEventArgs args) => BeginOcrEdit(OcrResult.Text);

    void BeginOcrEdit(string text)
    {
        if (busy || disposed || (pendingOcr == null && savedOcr == null)) return;
        editingOcr = true;
        ResetOcrImageSelection();
        OcrResult.IsReadOnly = false;
        OcrResult.Text = text;
        Status.Text = "";
        RefreshPin();
        OcrResult.Focus(FocusState.Programmatic);
    }

    void FinishOcrEdit(string text)
    {
        editingOcr = false;
        OcrResult.IsReadOnly = true;
        OcrResult.Text = text;
        RefreshPin();
        OcrResult.Focus(FocusState.Programmatic);
    }

    void CancelOcrEditClicked(object sender, RoutedEventArgs args)
    {
        FinishOcrEdit(savedOcr?.Text ?? pendingOcr?.Recognition.Text ?? "");
        Status.Text = "";
    }

    async void SaveOcrClicked(object sender, RoutedEventArgs args) => await Run(async token =>
    {
        var text = OcrResult.Text;
        try
        {
            if (savedOcr is { } record) savedOcr = await Task.Run(() => ocrHistory.Revise(record, text), token);
            else if (pendingOcr is { } pending) savedOcr = await Task.Run(() => ocrHistory.Save(pending, text), token);
            if (token.IsCancellationRequested || savedOcr == null) return;
            pendingOcr = null;
            FinishOcrEdit(savedOcr.Text);
            Status.Text = Strings.Screenshots_OcrSaved;
        }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error) || error is System.Text.Json.JsonException or InvalidOperationException)
        {
            if (!disposed) Status.Text = Strings.Settings_SaveFailed;
        }
    });

    void OcrVersionsOpening(object sender, object args)
    {
        OcrVersions.Items.Clear();
        if (savedOcr is not { } record) return;
        AddVersion(Strings.Screenshots_OcrOriginal, record.Original.Text);
        foreach (var correction in record.Corrections.Skip(1))
            AddVersion(correction.Saved.ToLocalTime().ToString("G"), correction.Text);

        void AddVersion(string label, string text)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => BeginOcrEdit(text);
            OcrVersions.Items.Add(item);
        }
    }

    async void DeleteOcrClicked(object sender, RoutedEventArgs args) => await Run(async token =>
    {
        if (savedOcr is not { } record) return;
        await Task.Run(() => ocrHistory.Delete(record.Id), token);
        if (token.IsCancellationRequested) return;
        if (viewingOcrHistory) BindOcrHistory();
        else ShowImage();
    });
}
