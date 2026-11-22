using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Hosting;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class DesktopScreenshotView : UserControl, IPanelView, IDisposable
{
    internal DesktopScreenshotView(string path, bool canEdit)
    {
        InitializeComponent();
        EditButton.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
        Apply(path);
    }

    internal void Apply(string path)
    {
        ImageError.Visibility = Visibility.Collapsed;
        Preview.Source = new BitmapImage { DecodePixelWidth = 512, DecodePixelType = DecodePixelType.Physical, UriSource = new Uri(path) };
    }

    public Size PreferredSize => new(256, 170);
    public UIElement DragRegion => Header;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler? IPanelView.CloseRequested { add { } remove { } }
    internal event EventHandler? OpenRequested;
    internal event EventHandler? EditRequested;
    internal event EventHandler? UnpinRequested;
    internal void OpenImage() => OpenRequested?.Invoke(this, EventArgs.Empty);

    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }
    public void Dispose() => Preview.Source = null;

    void ImageFailed(object sender, ExceptionRoutedEventArgs args) => ImageError.Visibility = Visibility.Visible;
    void OpenClicked(object sender, RoutedEventArgs args) => OpenImage();
    void EditClicked(object sender, RoutedEventArgs args) => EditRequested?.Invoke(this, EventArgs.Empty);
    void UnpinClicked(object sender, RoutedEventArgs args) => UnpinRequested?.Invoke(this, EventArgs.Empty);
}
