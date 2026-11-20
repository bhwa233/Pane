using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Views.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Pane.Views;

public sealed partial class NotesView
{
    CancellationTokenSource? fileOperation;
    bool previewing;

    void UpdateDocumentActions()
    {
        var editing = ready && historyNoteId == null && !ViewingRecycle;
        PreviewButton.IsEnabled = editing && MarkdownEditing && (previewing || store.Draft?.Body.Length > 0);
        FileButton.IsEnabled = editing && fileOperation == null;
        NoteImageButton.IsEnabled = editing && fileOperation == null && CanAddImage;
        ExportFileItem.IsEnabled = editing && (store.DraftIsSaved || store.HasChanges);
        var label = previewing ? Strings.Common_Edit : Strings.Notes_Preview;
        PreviewIcon.Glyph = previewing ? "pencil" : "eye";
        AutomationProperties.SetName(PreviewButton, label);
        ToolTipService.SetToolTip(PreviewButton, label);
        RefreshToolbar();
    }

    void PreviewClicked(object sender, RoutedEventArgs args)
    {
        if (previewing)
        {
            SetPreview(false);
            BodyBox.Focus(FocusState.Programmatic);
            return;
        }
        if (!MarkdownEditing || store.Draft is not { Body.Length: > 0 } draft) return;
        if (draft.Body.Length > 40_000 || draft.Body.Count(character => character == '\n') > 600)
        {
            Status.Text = Strings.Notes_PreviewTooLong;
            return;
        }
        var style = new MarkdownStyle
        {
            Font = BodyBox.FontFamily, Mono = MonoProto.FontFamily, MathFont = MathProto.FontFamily,
            Size = BodyBox.FontSize, LineHeight = 24,
            Text1 = BodyBox.Foreground, Text2 = MonoProto.Foreground, Text3 = Text3Proto.Foreground,
            Stroke = CodeProto.BorderBrush, CodeFill = CodeProto.Background, Accent = (SolidColorBrush)MathProto.Foreground,
        };
        foreach (var block in MarkdownRender.Build(draft.Body, style, null, out _,
            (row, task) => AddTaskReminderButton(row, draft.Id, task))) PreviewBlocks.Children.Add(block);
        SetPreview(true);
        MarkdownPreview.ChangeView(null, 0, null, disableAnimation: true);
        MarkdownPreview.Focus(FocusState.Programmatic);
    }

    void SetPreview(bool visible)
    {
        previewing = visible;
        BodyBox.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        MarkdownPreview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            if (customReminderTask is not null) CustomReminderFlyout.Hide();
            PreviewBlocks.Children.Clear();
        }
        UpdateDocumentActions();
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
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".txt");
        if (await picker.PickSingleFileAsync() is not { } file || token.IsCancellationRequested) return;
        var (title, body) = await Task.Run(() => NoteStore.ReadFile(file.Path), token);
        if (token.IsCancellationRequested || !TrySave()) return;
        store.ImportFile(title, body);
        SearchBox.Text = "";
        CategoryFilter.SelectedIndex = 0;
        PresentDraft();
        Status.Text = Strings.Notes_ImportedFile;
        TitleBox.Focus(FocusState.Programmatic);
    }, Strings.Notes_ImportFileFailed);

    async void ExportFileClicked(object sender, RoutedEventArgs args) => await RunFileOperation(async token =>
    {
        if (store.Draft is not { } note) return;
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedFileName = Strings.Module_Notes_Name };
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        picker.FileTypeChoices.Add(Strings.Notes_TextFile, [".txt"]);
        if (await picker.PickSaveFileAsync() is not { } file || token.IsCancellationRequested) return;
        await Task.Run(() => store.ExportFile(note, file.Path, token), token);
        if (!token.IsCancellationRequested) Status.Text = Strings.Notes_ExportedFile;
    }, Strings.Notes_ExportFileFailed);
}
