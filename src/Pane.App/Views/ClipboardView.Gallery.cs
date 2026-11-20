using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using Pane.Localization;
using Pane.Services.Clipboard;
using Pane.Views.Clipboard;

namespace Pane.Views;

public sealed partial class ClipboardView
{
    CancellationTokenSource? fileOperation;
    bool FilePickerOpen { get; set; }
    internal bool CanHideOnBlur => !FilePickerOpen;
    bool CanUseFilePicker => visible && !ForLauncher && fileOperation is null;

    async void OnGalleryImport(object sender, RoutedEventArgs args)
    {
        if (!CanUseFilePicker) return;
        if (Passive) EngageRequested?.Invoke(this, EventArgs.Empty);
        using var operation = new CancellationTokenSource();
        fileOperation = operation;
        var group = collection is { Length: > 0 } selected ? selected : Strings.Clipboard_Gallery;
        try
        {
            SetFileStatus(null);
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(extension);
            FilePickerOpen = true;
            var files = await picker.PickMultipleFilesAsync();
            FilePickerOpen = false;
            if (operation.IsCancellationRequested || files is not { Count: > 0 }) return;
            if (files.Count > ClipboardHistory.GalleryBatchLimit)
            {
                SetFileStatus(string.Format(Strings.Clipboard_GalleryBatchLimit, ClipboardHistory.GalleryBatchLimit));
                return;
            }
            SetFileStatus(Strings.Clipboard_GalleryImporting);
            var result = await history.ImportGalleryAsync(files.Select(file => file.Path).ToArray(), group, operation.Token);
            if (operation.IsCancellationRequested) return;
            if (result.Added + result.Existing > 0)
            {
                collection = group;
                filter = "image";
                if (Input.Text.Length > 0) Input.Text = string.Empty;
                else Render(keepSelection: false);
                FocusSearch();
            }
            SetFileStatus(result.Failed > 0 && result.Added + result.Existing == 0 ? Strings.Clipboard_GalleryFailed :
                string.Format(Strings.Clipboard_GalleryResult, result.Added, result.Existing, result.Failed));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception) { if (!operation.IsCancellationRequested) SetFileStatus(Strings.Clipboard_GalleryFailed); }
        finally
        {
            FilePickerOpen = false;
            fileOperation = null;
        }
    }

    async void ExportFile(ClipRow row)
    {
        if (!CanUseFilePicker || row.ExportExtension is not { } extension) return;
        if (Passive) EngageRequested?.Invoke(this, EventArgs.Empty);
        using var operation = new CancellationTokenSource();
        fileOperation = operation;
        try
        {
            SetFileStatus(null);
            var image = extension == ".png";
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedFileName = image ? Path.GetFileNameWithoutExtension(string.Format(Strings.Clipboard_ImageFileName, row.Entry.CopiedAt.LocalDateTime)) : Strings.Common_Clipboard
            };
            picker.FileTypeChoices.Add(image ? "PNG" : Strings.Notes_TextFile, [extension]);
            FilePickerOpen = true;
            var file = await picker.PickSaveFileAsync();
            FilePickerOpen = false;
            if (file is null || operation.IsCancellationRequested) return;
            await history.ExportFileAsync(row.Entry.Id, file.Path, operation.Token);
            if (!operation.IsCancellationRequested) SetFileStatus(Strings.Clipboard_FileExported);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception) { if (!operation.IsCancellationRequested) SetFileStatus(Strings.Clipboard_FileExportFailed); }
        finally
        {
            FilePickerOpen = false;
            fileOperation = null;
        }
    }

    void SetFileStatus(string? message)
    {
        FileStatus.Text = message ?? string.Empty;
        FileStatus.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
