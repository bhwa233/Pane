using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Hosting;
using Pane.Localization;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

/// <summary>胶囊成员的弹层条目。</summary>
internal sealed record DesktopNoteEntry(Guid Id, string Title, bool Hidden, bool Folded, bool Reminder);

/// <summary>「桌面便签」键盘弹层（notes-mvp §7）：列出所有贴到桌面的便签（含已隐藏），
/// 方向键选择、回车展开、Esc 关闭；胶囊本身不进入其他应用的 Tab 顺序。</summary>
public sealed partial class DesktopNotesPanelView : UserControl, IPanelView
{
    readonly Action<Guid> open;
    readonly Action close;
    Guid? selected;

    internal DesktopNotesPanelView(IReadOnlyList<DesktopNoteEntry> entries, Action<Guid> open, Action close)
    {
        this.open = open;
        this.close = close;
        InitializeComponent();
        NoteList.Items.Clear();
        foreach (var entry in entries) NoteList.Items.Add(Row(entry));
        EmptyList.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteList.SelectedIndex = entries.Count > 0 ? 0 : -1;
        if (entries.Count > 0) selected = entries[0].Id;
    }

    public Size PreferredSize => new(300, Math.Clamp(NoteList.Items.Count * 40 + 64, 132, 420));
    public UIElement DragRegion => this;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler? IPanelView.CloseRequested { add { } remove { } }
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }

    ListViewItem Row(DesktopNoteEntry entry)
    {
        var title = entry.Title.Length == 0 ? Strings.Notes_Untitled : entry.Title;
        var content = new StackPanel { Spacing = 2 };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (entry.Hidden)
        {
            line.Children.Add(new TextBlock { Text = Strings.Notes_FilterHidden, FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["PaneText3Brush"], VerticalAlignment = VerticalAlignment.Center });
        }
        line.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(line);
        if (entry.Reminder)
            content.Children.Add(new TextBlock { Text = Strings.Notes_Reminder, FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["PaneText3Brush"] });
        var row = new ListViewItem { Tag = entry.Id, Content = content };
        row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(row, title);
        return row;
    }

    void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        switch (args.Key)
        {
            case VirtualKey.Enter:
                if (NoteList.SelectedItem is ListViewItem { Tag: Guid id }) open(id);
                close();
                args.Handled = true;
                break;
            case VirtualKey.Escape:
                close();
                args.Handled = true;
                break;
        }
    }
}
