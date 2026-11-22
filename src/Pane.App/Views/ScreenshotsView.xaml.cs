using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Localization;
using Pane.Services.Clipboard;
using Pane.Services.Notes;
using Pane.Services.Screenshots;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using SystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Pane.Views;

public sealed partial class ScreenshotsView : UserControl, IDisposable
{
    readonly ScreenshotStore store;
    readonly NativeScreenshotCapture capture;
    readonly NativeScreenshotOcr ocr;
    readonly OcrHistoryStore ocrHistory;
    readonly Func<Guid, bool> isPinned;
    readonly Action<Guid> togglePin;
    readonly Action<Guid> edited;
    readonly Func<bool> limitHistory;
    readonly Action<bool> setLimitHistory;
    readonly Action<string, bool> openTranslation;
    readonly Func<string, string, bool> saveNote;
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? recognition;
    bool busy, disposed, previewReady, loadingHistory;
    Guid? selected;

    internal ScreenshotsView(ScreenshotStore store, NativeScreenshotCapture capture, NativeScreenshotOcr ocr, OcrHistoryStore ocrHistory, Func<Guid, bool> isPinned,
        Action<Guid> togglePin, Action<Guid> edited, Func<bool> limitHistory, Action<bool> setLimitHistory,
        Action<string, bool> openTranslation, Func<string, string, bool> saveNote)
    {
        this.store = store;
        this.capture = capture;
        this.ocr = ocr;
        this.ocrHistory = ocrHistory;
        this.isPinned = isPinned;
        this.togglePin = togglePin;
        this.edited = edited;
        this.limitHistory = limitHistory;
        this.setLimitHistory = setLimitHistory;
        this.openTranslation = openTranslation;
        this.saveNote = saveNote;
        InitializeComponent();
        InitializeScreenshotMode();
    }

    public Size PreferredSize => new(620, 430);
    public Size MinimumSize => new(480, 320);
    internal void EnsureHistoryLoaded() { if (!viewingOcrHistory && History.Items.Count == 0) LoadHistory(selected); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        Preview.Source = null;
        OcrPreview.Source = null;
        ResetOcrImageSelection();
        ocrGeometry = null;
        pendingOcr = null;
        savedOcr = null;
    }

    internal void Open(Guid id)
    {
        if (busy || editingOcr) return;
        screenshotMode.SetValue(ScreenshotMode);
        ShowScreenshotMode();
        LoadHistory(id);
    }

    void LoadHistory(Guid? target)
    {
        loadingHistory = true;
        try
        {
            var entries = store.List();
            History.ItemsSource = entries.Select(entry => new ListViewItem
                { Tag = entry.Id, Content = entry.CreatedUtc.ToLocalTime().ToString("g") }).ToArray();
            var index = Array.FindIndex(entries, entry => entry.Id == target);
            History.SelectedIndex = index >= 0 ? index : entries.Length > 0 ? 0 : -1;
        }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error)) { Status.Text = Strings.Screenshots_LoadFailed; return; }
        finally { loadingHistory = false; }
        SelectImage();
    }

    void SelectionChanged(object sender, SelectionChangedEventArgs args) { if (!loadingHistory) SelectImage(); }

    void SelectImage()
    {
        DeleteButton.Flyout.Hide();
        recognition?.Cancel();
        ShowImage();
        selected = (History.SelectedItem as ListViewItem)?.Tag as Guid?;
        previewReady = false;
        Status.Text = selected == null ? Strings.Screenshots_Empty : "";
        Preview.Source = selected is { } id ? new BitmapImage
        {
            DecodePixelWidth = 960, DecodePixelType = DecodePixelType.Physical, UriSource = new Uri(store.PathFor(id))
        } : null;
        RefreshPin();
    }

    internal void RefreshPin()
    {
        var pinned = selected is { } id && isPinned(id);
        var editable = selected is { } imageId && store.IsEditable(imageId);
        EditButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        EditButton.IsEnabled = !busy && editable && NativeScreenshotCapture.Available;
        ToolTipService.SetToolTip(EditButton, NativeScreenshotCapture.Available ? Strings.Screenshots_Edit : Strings.Screenshots_CaptureUnavailable);
        CopyButton.IsEnabled = !busy && previewReady;
        ExportButton.IsEnabled = !busy && previewReady;
        OcrButton.IsEnabled = !busy && previewReady;
        ClipboardOcrButton.IsEnabled = !busy && !editingOcr;
        CopyOcrButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(OcrResult.Text);
        OcrTextActionsButton.IsEnabled = CopyOcrButton.IsEnabled && !editingOcr;
        RefreshOcrSelectionAction();
        RefreshOcrHistoryActions();
        PinButton.IsEnabled = !busy && selected != null && (previewReady || pinned);
        DeleteButton.IsEnabled = !busy && selected != null && !pinned;
        ClearHistoryButton.IsEnabled = !busy && !editingOcr;
        ClearHistoryItem.IsEnabled = UnpinnedHistory().Any();
        LimitHistoryItem.IsChecked = limitHistory();
        var deleteLabel = pinned ? Strings.Screenshots_UnpinToDelete : Strings.Common_Delete;
        AutomationProperties.SetName(DeleteButton, deleteLabel);
        ToolTipService.SetToolTip(DeleteButton, deleteLabel);
        CaptureButton.IsEnabled = !busy && !editingOcr && NativeScreenshotCapture.Available;
        ToolTipService.SetToolTip(CaptureButton, NativeScreenshotCapture.Available ? Strings.Screenshots_Capture : Strings.Screenshots_CaptureUnavailable);
        ImportButton.IsEnabled = !busy && !editingOcr;
        PinIcon.Glyph = pinned ? "pin-off" : "pin";
        var label = pinned ? Strings.Notes_UnpinDesktop : Strings.Notes_PinDesktop;
        AutomationProperties.SetName(PinButton, label);
        ToolTipService.SetToolTip(PinButton, label);
    }

    async void CaptureClicked(object sender, RoutedEventArgs args) => await CaptureNewAsync();

    internal Task CaptureNewAsync() => Run(async token =>
    {
        if (editingOcr || capture.IsRunning || !NativeScreenshotCapture.Available) return;
        if (await capture.CaptureAsync(token) is not { } result || disposed) return;
        await ShowCapturedAsync(result.Id, result.Recognize, token);
    });

    internal Task ShowCapturedAsync(Guid id, bool recognize) => Run(token => ShowCapturedAsync(id, recognize, token));

    async Task ShowCapturedAsync(Guid id, bool recognize, CancellationToken token)
    {
        var historyTrimmed = LoadNewScreenshot(id);
        if (recognize && selected == id) await RecognizeImageAsync(id, token);
        if (!disposed && !historyTrimmed && string.IsNullOrEmpty(Status.Text)) Status.Text = Strings.Screenshots_ClearHistoryFailed;
    }

    async void EditClicked(object sender, RoutedEventArgs args)
    {
        if (selected is { } id) await EditSelectedAsync(id);
    }

    internal Task EditSelectedAsync(Guid id) => Run(async token =>
    {
        if (selected != id || editingOcr || viewingOcrHistory || !store.IsEditable(id) || !NativeScreenshotCapture.Available) return;
        if (await capture.EditAsync(id, token) is not { } result) return;
        edited(result);
        if (!disposed && selected == result) SelectImage();
    });

    async void ImportClicked(object sender, RoutedEventArgs args) => await Run(async token =>
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
        if (await picker.PickSingleFileAsync() is not { } file || token.IsCancellationRequested) return;
        var id = await Task.Run(() => store.Import(file.Path), token);
        if (!disposed && !LoadNewScreenshot(id) && string.IsNullOrEmpty(Status.Text)) Status.Text = Strings.Screenshots_ClearHistoryFailed;
    });

    bool LoadNewScreenshot(Guid id)
    {
        var succeeded = true;
        if (limitHistory())
        {
            Preview.Source = null;
            try { succeeded = store.TrimUnpinnedHistory(id, isPinned); }
            catch (Exception error) when (ScreenshotStore.IsStorageError(error)) { succeeded = false; }
        }
        LoadHistory(id);
        return succeeded;
    }

    async void CopyClicked(object sender, RoutedEventArgs args) => await Run(async token =>
    {
        if (selected is not { } id) return;
        var file = await StorageFile.GetFileFromPathAsync(store.PathFor(id));
        if (token.IsCancellationRequested) return;
        var content = new DataPackage();
        content.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        SystemClipboard.SetContent(content);
        SystemClipboard.Flush();
    });

    async void ExportClicked(object sender, RoutedEventArgs args) => await Run(async token =>
    {
        if (selected is not { } id || !previewReady) return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedFileName = $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("PNG", [".png"]);
        if (await picker.PickSaveFileAsync() is not { } file || token.IsCancellationRequested) return;
        await Task.Run(() => store.Export(id, file.Path, token), token);
        if (!disposed && selected == id) Status.Text = Strings.Screenshots_Exported;
    });

    async void OcrClicked(object sender, RoutedEventArgs args) => await Run(async token =>
    {
        if (selected is { } id && previewReady) await RecognizeImageAsync(id, token);
    });

    async void ClipboardOcrClicked(object sender, RoutedEventArgs args) => await Run(token => RecognizeAsync(selected, async request =>
    {
        var png = await WindowsClipboardCapture.ReadBitmapAsync(ScreenshotStore.MaxFileBytes, ScreenshotStore.MaxOcrPixels, request);
        return png == null ? null : await ocr.RecognizeAsync(png, request);
    }, token));

    Task RecognizeImageAsync(Guid id, CancellationToken token) => RecognizeAsync(id, async request => await ocr.RecognizeAsync(id, request), token);

    async Task RecognizeAsync(Guid? id, Func<CancellationToken, Task<OcrImageResult?>> recognize, CancellationToken token)
    {
        ShowImage();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        recognition = request;
        Status.Text = Strings.Screenshots_OcrWorking;
        ImageActions.Visibility = Visibility.Collapsed;
        OcrProgressActions.Visibility = Visibility.Visible;
        OcrSpinner.IsActive = true;
        CancelOcrButton.IsEnabled = true;
        CancelOcrButton.Focus(FocusState.Programmatic);
        try
        {
            var result = await recognize(request.Token);
            if (disposed || selected != id || request.IsCancellationRequested) return;
            if (result == null)
            {
                Status.Text = Strings.Screenshots_ClipboardNoImage;
                return;
            }
            var bitmap = await DecodeOcrPreviewAsync(result.Image, result.Recognition);
            if (disposed || selected != id || request.IsCancellationRequested) return;
            pendingOcr = result;
            PresentOcr(bitmap, result.Recognition, result.Recognition.Text);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (!disposed && selected == id) Status.Text = "";
        }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error) || error is System.ComponentModel.Win32Exception or
            System.Text.Json.JsonException or TimeoutException or InvalidOperationException or KeyNotFoundException or FormatException or
            System.Runtime.InteropServices.COMException)
        {
            if (!disposed && selected == id) Status.Text = error is InvalidOperationException
                ? error.Message : Strings.Screenshots_OcrFailed;
        }
        finally
        {
            recognition = null;
            if (!disposed)
            {
                OcrSpinner.IsActive = false;
                OcrProgressActions.Visibility = Visibility.Collapsed;
                if (OcrWorkspace.Visibility != Visibility.Visible) ImageActions.Visibility = Visibility.Visible;
            }
        }
    }

    async void CopyOcrClicked(object sender, RoutedEventArgs args) => await CopyOcrText(OcrResult.Text);

    async void CopyOcrSelectionClicked(object sender, RoutedEventArgs args) => await CopyOcrText(OcrResult.SelectedText);

    string? OcrActionText()
    {
        if (busy || disposed || editingOcr || OcrWorkspace.Visibility != Visibility.Visible) return null;
        var text = OcrResult.SelectionLength > 0 ? OcrResult.SelectedText : OcrResult.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    void LookupOcrClicked(object sender, RoutedEventArgs args)
    {
        if (OcrActionText() is { } text) openTranslation(text, true);
    }

    void TranslateOcrClicked(object sender, RoutedEventArgs args)
    {
        if (OcrActionText() is { } text) openTranslation(text, false);
    }

    void SaveOcrNoteClicked(object sender, RoutedEventArgs args)
    {
        if (OcrActionText() is not { } text) return;
        try { Status.Text = saveNote(Strings.Screenshots_OcrResult, text) ? Strings.Notes_ResultSaved : Strings.Notes_ResultModuleDisabled; }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Notes_ResultSaveFailed; }
    }

    Task CopyOcrText(string text) => Run(_ =>
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;
        var content = new DataPackage();
        content.SetText(text);
        SystemClipboard.SetContent(content);
        SystemClipboard.Flush();
        return Task.CompletedTask;
    });

    void CancelOcrClicked(object sender, RoutedEventArgs args)
    {
        CancelOcrButton.IsEnabled = false;
        recognition?.Cancel();
    }

    void BackToImageClicked(object sender, RoutedEventArgs args)
    {
        if (viewingOcrHistory)
        {
            OcrHistoryList.SelectedIndex = -1;
            ShowOcrHistoryEmptyState();
            OcrHistoryList.Focus(FocusState.Programmatic);
            return;
        }
        ShowImage();
        Status.Text = "";
        (previewReady ? OcrButton : ClipboardOcrButton).Focus(FocusState.Programmatic);
    }

    void ShowImage()
    {
        OcrWorkspace.Visibility = OcrResultActions.Visibility = OcrEditActions.Visibility = Visibility.Collapsed;
        pendingOcr = null;
        savedOcr = null;
        editingOcr = false;
        ocrGeometry = null;
        ResetOcrImageSelection();
        OcrPreview.Source = null;
        OcrResult.Text = "";
        OcrResult.IsReadOnly = true;
        PreviewFrame.Visibility = viewingOcrHistory ? Visibility.Collapsed : Visibility.Visible;
        if (recognition == null) ImageActions.Visibility = PreviewFrame.Visibility;
    }

    async Task Run(Func<CancellationToken, Task> operation)
    {
        if (busy || disposed) return;
        busy = true;
        var token = lifetime.Token;
        RefreshPin();
        try { await operation(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error) || error is System.ComponentModel.Win32Exception or
            System.Runtime.InteropServices.COMException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { if (!disposed) Status.Text = Strings.Screenshots_LoadFailed; }
        finally { busy = false; if (!disposed) RefreshPin(); }
    }

    void PinClicked(object sender, RoutedEventArgs args) { if (selected is { } id) togglePin(id); }

    IEnumerable<Guid> UnpinnedHistory() => History.Items.OfType<ListViewItem>()
        .Select(item => (Guid)item.Tag).Where(id => !isPinned(id));

    void LimitHistoryClicked(object sender, RoutedEventArgs args)
    {
        try { setLimitHistory(LimitHistoryItem.IsChecked); }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error))
        {
            LimitHistoryItem.IsChecked = limitHistory();
            Status.Text = Strings.Settings_SaveFailed;
        }
    }

    void ClearHistoryClicked(object sender, RoutedEventArgs args) => DispatcherQueue.TryEnqueue(ClearHistory);

    void ClearHistory()
    {
        if (busy || disposed) return;
        var entries = UnpinnedHistory().ToArray();
        if (entries.Length == 0) return;
        if (selected is { } id && entries.Contains(id)) Preview.Source = null;
        var failed = false;
        foreach (var entry in entries)
        {
            try { store.Delete(entry); }
            catch (Exception error) when (ScreenshotStore.IsStorageError(error)) { failed = true; }
        }
        LoadHistory(selected);
        Status.Text = failed ? Strings.Screenshots_ClearHistoryFailed : Strings.Screenshots_ClearHistoryDone;
    }

    void DeleteClicked(object sender, RoutedEventArgs args)
    {
        if (busy || disposed || selected is not { } id || isPinned(id)) return;
        Preview.Source = null;
        try { store.Delete(id); }
        catch (Exception error) when (ScreenshotStore.IsStorageError(error))
        {
            SelectImage();
            Status.Text = Strings.Screenshots_DeleteFailed;
            return;
        }
        LoadHistory(null);
        History.Focus(FocusState.Programmatic);
    }

    void ImageOpened(object sender, RoutedEventArgs args) { previewReady = true; RefreshPin(); }
    void ImageFailed(object sender, ExceptionRoutedEventArgs args) { previewReady = false; Status.Text = Strings.Screenshots_LoadFailed; RefreshPin(); }
}
