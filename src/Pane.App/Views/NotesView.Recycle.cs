using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Localization;
using Pane.Services.Notes;

namespace Pane.Views;

public sealed partial class NotesView
{
    bool ViewingRecycle => RecyclePane.Visibility == Visibility.Visible;
    NoteDocument? SelectedRecycledNote => (RecycleList.SelectedItem as ListViewItem)?.Tag as NoteDocument;

    void RecycleBinClicked(object sender, RoutedEventArgs args)
    {
        if (!ready || fileOperation != null) return;
        if (ViewingRecycle) { RecycleBackClicked(sender, args); return; }
        if (!TrySave()) return;
        CloseHistory();
        SetPreview(false);
        NotesWorkspace.Visibility = Visibility.Collapsed;
        RecyclePane.Visibility = Visibility.Visible;
        ReloadRecycleList();
        UpdateDocumentActions();
    }

    void ExitRecycle()
    {
        if (!ViewingRecycle) return;
        RecycleList.Items.Clear();
        RecyclePane.Visibility = Visibility.Collapsed;
        NotesWorkspace.Visibility = Visibility.Visible;
        UpdateDocumentActions();
    }

    void RecycleBackClicked(object sender, RoutedEventArgs args)
    {
        ExitRecycle();
        RecycleBinButton.Focus(FocusState.Programmatic);
    }

    void ReloadRecycleList()
    {
        RecycleList.Items.Clear();
        foreach (var note in store.RecycledDocuments) RecycleList.Items.Add(NoteRow(note, note));
        RecycleEmpty.Visibility = RecycleList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecycleList.SelectedIndex = RecycleList.Items.Count > 0 ? 0 : -1;
        UpdateRecycleSelection();
        if (RecycleList.SelectedItem is Control selected) selected.Focus(FocusState.Programmatic);
        else RecycleBackButton.Focus(FocusState.Programmatic);
    }

    void RecycleSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ready) UpdateRecycleSelection();
    }

    void UpdateRecycleSelection()
    {
        var note = SelectedRecycledNote;
        RecycleBody.Text = note?.Body ?? "";
        RecycleBody.Visibility = note != null ? Visibility.Visible : Visibility.Collapsed;
        RestoreNoteButton.IsEnabled = PurgeNoteButton.IsEnabled = note != null;
        RecycleStatus.Text = "";
    }

    void RestoreNoteClicked(object sender, RoutedEventArgs args)
    {
        if (SelectedRecycledNote is not { } note) return;
        try { if (!store.RestoreRecycled(note.Id)) return; }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { RecycleStatus.Text = Strings.Notes_RecycleRestoreFailed; return; }
        OpenNote(note.Id);
        Status.Text = Strings.Notes_RecycleRestored;
        TitleBox.Focus(FocusState.Programmatic);
    }

    void PurgeNoteClicked(object sender, RoutedEventArgs args)
    {
        if (SelectedRecycledNote is not { } note) return;
        try { if (!store.DeleteRecycled(note.Id)) return; }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { RecycleStatus.Text = Strings.Notes_DeleteFailed; return; }
        ReloadRecycleList();
    }
}
