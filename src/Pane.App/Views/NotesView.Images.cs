using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Pane.Localization;
using Pane.Services.Clipboard;
using Pane.Services.Notes;
using Pane.Views.Controls;

namespace Pane.Views;

public sealed partial class NotesView
{
    bool CanAddImage => store.Draft is { } draft && (draft.Images?.Length ?? 0) < NoteStore.ImageLimit;

    void BodyPaste(object sender, TextControlPasteEventArgs args)
    {
        if (!WindowsClipboardCapture.HasBitmap()) return;
        args.Handled = true;
        if (fileOperation != null) return;
        if (!CanAddImage) { Status.Text = Strings.Notes_ImageLimit; return; }
        _ = AttachClipboardImage();
    }

    async void PasteImageClicked(object sender, RoutedEventArgs args) => await AttachClipboardImage();

    Task AttachClipboardImage() => AttachImage(async token =>
    {
        var png = await WindowsClipboardCapture.ReadBitmapAsync(NoteStore.ImageByteLimit, NoteStore.ImagePixelLimit, token);
        if (png == null && !token.IsCancellationRequested) Status.Text = Strings.Screenshots_ClipboardNoImage;
        return png;
    });

    async void ImportImageClicked(object sender, RoutedEventArgs args) => await AttachImage(async token =>
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add(".png");
        if (await picker.PickSingleFileAsync() is not { } file || token.IsCancellationRequested) return null;
        return await Task.Run(() =>
        {
            using var stream = File.OpenRead(file.Path);
            NoteStore.ValidateImage(stream);
            stream.Position = 0;
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }, token);
    });

    Task AttachImage(Func<CancellationToken, Task<byte[]?>> read) => RunFileOperation(async token =>
    {
        if (!CanAddImage || store.Draft is not { } draft) return;
        var png = await read(token);
        if (png == null || token.IsCancellationRequested || store.Draft?.Id != draft.Id || historyNoteId != null) return;
        store.AddImage(png);
        RefreshNoteImages();
        TrySave();
        UpdateButtons();
    }, Strings.Notes_ImageFailed);

    void RefreshNoteImages() => ShowImages(NoteImages, NoteImageScroller, store.Draft, id =>
    {
        store.RemoveImage(id);
        RefreshNoteImages();
        TrySave();
        UpdateButtons();
        BodyBox.Focus(FocusState.Programmatic);
    });

    void ShowImages(StackPanel panel, ScrollViewer scroller, NoteDocument? note, Action<Guid>? remove = null)
    {
        panel.Children.Clear();
        scroller.Visibility = note?.Images is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (note?.Images is not { } images) return;
        foreach (var id in images)
        {
            var tile = new Grid { Width = 84, Height = 66 };
            var hint = new PaneIcon { Glyph = "image", Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(hint, Strings.Notes_ImageUnavailable);
            tile.Children.Add(hint);
            if (store.FindImage(note.Id, id) is { } path)
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 256, DecodePixelType = DecodePixelType.Physical, UriSource = new Uri(path) };
                var thumbnail = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 66 };
                AutomationProperties.SetName(thumbnail, Strings.Notes_Images);
                thumbnail.ImageOpened += (_, _) => hint.Visibility = Visibility.Collapsed;
                tile.Children.Add(thumbnail);
            }
            if (remove != null)
            {
                var button = new Button { Style = (Style)Application.Current.Resources["PaneIconButton"], IsTabStop = true,
                    Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Content = new PaneIcon { Glyph = "close", Width = 12, Height = 12 } };
                AutomationProperties.SetName(button, Strings.Notes_RemoveImage);
                ToolTipService.SetToolTip(button, Strings.Notes_RemoveImage);
                button.Click += (_, _) => remove(id);
                tile.Children.Add(button);
            }
            panel.Children.Add(tile);
        }
    }
}
