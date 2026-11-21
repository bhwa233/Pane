using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Hosting;
using Pane.Localization;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class NoteReminderView : UserControl, IPanelView
{
    readonly Action open;

    internal NoteReminderView(string title, Action open)
    {
        this.open = open;
        InitializeComponent();
        NoteTitle.Text = string.IsNullOrWhiteSpace(title) ? Strings.Notes_Untitled : title;
    }

    void CloseClicked(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);

    void OpenClicked(object sender, RoutedEventArgs args)
    {
        open();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public Size PreferredSize => new(320, 132);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public UIElement DragRegion => Header;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }
}
