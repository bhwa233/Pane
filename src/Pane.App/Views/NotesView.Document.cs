using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Pane.Localization;
using Pane.Services.Notes;
using Microsoft.Windows.Storage.Pickers;

namespace Pane.Views;

/// <summary>文件操作（notes-mvp §3）：只支持 .txt。导入为独立新便签（多选、部分失败汇总），
/// 另存只写当前纯文本正文；取消、坏编码、越限和读写失败不留空记录。</summary>
public sealed partial class NotesView
{
    CancellationTokenSource? fileOperation;

    void UpdateDocumentActions()
    {
        var editing = ready && historyNoteId == null && !ViewingRecycle;
        FileButton.IsEnabled = editing && fileOperation == null;
        ExportFileItem.IsEnabled = editing && current is { Persisted: true };
    }

    async Task RunFileOperation(Func<CancellationToken, Task> action, string failure)
    {
        if (!ready || disposed || historyNoteId != null || fileOperation != null) return;
        using var tokenSource = new CancellationTokenSource();
        fileOperation = tokenSource;
        UpdateDocumentActions();
        try { await action(tokenSource.Token); }
        catch (OperationCanceledException) when (tokenSource.IsCancellationRequested) { }
        catch (Exception error) when (NoteStore.IsStorageError(error) || error is DecoderFallbackException or COMException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!tokenSource.IsCancellationRequested) Status.Text = failure;
        }
        finally
        {
            fileOperation = null;
            if (!disposed) UpdateDocumentActions();
        }
    }

    async void ImportFileClicked(object sender, RoutedEventArgs args) => await RunFileOperation(async token =>
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add(".txt");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0 || token.IsCancellationRequested) return;
        var imported = 0;
        var failures = new List<string>();
        foreach (var file in files)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                var (title, body) = await Task.Run(() => NoteStore.ReadFile(file.Path), token);
                if (token.IsCancellationRequested) break;
                await Task.Run(() => store.Create(title, body), token);
                imported++;
            }
            catch (Exception error) when (NoteStore.IsStorageError(error) || error is DecoderFallbackException)
            {
                failures.Add(Path.GetFileName(file.Path));
            }
        }
        ReloadTags();
        ReloadList();
        if (imported > 0 && failures.Count == 0)
        {
            Status.Text = files.Count == 1 ? Strings.Notes_ImportedFile : string.Format(Strings.Notes_ImportedFiles, imported);
        }
        else if (imported > 0)
        {
            Status.Text = string.Format(Strings.Notes_ImportPartialFailed, string.Join("、", failures.Take(3)) + (failures.Count > 3 ? "…" : ""));
        }
        else if (failures.Count > 0) Status.Text = Strings.Notes_ImportFileFailed;
    }, Strings.Notes_ImportFileFailed);

    async void ExportFileClicked(object sender, RoutedEventArgs args) => await RunFileOperation(async token =>
    {
        if (current is not { Persisted: true } session) return;
        await ExportBody(session.Id, token);
    }, Strings.Notes_ExportFileFailed);

    async Task ExportBody(Guid id, CancellationToken token)
    {
        if (store.Find(id) is not { } note) return;
        var suggested = NoteStore.SuggestedFileName(note.Title, note.Body, Strings.Notes_Untitled);
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedFileName = suggested };
        picker.FileTypeChoices.Add(Strings.Notes_TextFile, [".txt"]);
        if (await picker.PickSaveFileAsync() is not { } file || token.IsCancellationRequested) return;
        await Task.Run(() => store.ExportFile(note, file.Path, token), token);
        if (!token.IsCancellationRequested) Status.Text = Strings.Notes_ExportedFile;
    }
}
