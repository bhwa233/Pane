using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Hosting;
using Pane.Localization;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class DesktopScreenshotView : UserControl, IPanelView, IDisposable
{
    string UnpinLabel { get; }
    internal event EventHandler<bool>? TopmostChanged;

    internal DesktopScreenshotView(string path, bool canEdit, bool inWorkspace, bool topmost)
    {
        UnpinLabel = inWorkspace ? Strings.CardDesktop_RemoveCard : Strings.Notes_UnpinDesktop;
        InitializeComponent();
        if (!canEdit) Actions.Items.Remove(EditItem);
        TopmostItem.IsChecked = topmost;
        Apply(path);
    }

    internal void Apply(string path)
    {
        ImageError.Visibility = Visibility.Collapsed;
        Preview.Source = new BitmapImage(new Uri(path));
    }

    public Size PreferredSize => new(256, 170);
    public UIElement DragRegion => Preview;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler? IPanelView.CloseRequested { add { } remove { } }
    internal event EventHandler? OpenRequested;
    internal event EventHandler? EditRequested;
    internal event EventHandler? UnpinRequested;
    internal void OpenImage() => OpenRequested?.Invoke(this, EventArgs.Empty);
    internal void ShowActions(Point position) => Actions.ShowAt(this, position);

    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }
    public void Dispose() => Preview.Source = null;

    void ImageFailed(object sender, ExceptionRoutedEventArgs args) => ImageError.Visibility = Visibility.Visible;
    void OpenClicked(object sender, RoutedEventArgs args) => OpenImage();
    void EditClicked(object sender, RoutedEventArgs args) => EditRequested?.Invoke(this, EventArgs.Empty);
    void UnpinClicked(object sender, RoutedEventArgs args) => UnpinRequested?.Invoke(this, EventArgs.Empty);
    void TopmostClicked(object sender, RoutedEventArgs args) => TopmostChanged?.Invoke(this, TopmostItem.IsChecked);
}
