using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Hosting;
using Windows.Foundation;

namespace Pane.Views;

/// <summary>全局拖动胶囊（notes-mvp §7「独立的临时拖动胶囊」）：拖动期间的跟手薄窗。
/// 由协调层用 GetCursorPos 轮询移动，Esc 取消，松手提交；它本身不处理任何输入。</summary>
public sealed partial class NoteCapsuleGhostView : UserControl, IPanelView
{
    public NoteCapsuleGhostView()
    {
        InitializeComponent();
    }

    public Size PreferredSize => new(NoteCapsuleGeometry.CapsuleWidth, NoteCapsuleGeometry.CapsuleHeight);
    public UIElement DragRegion => Pill;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler? IPanelView.CloseRequested { add { } remove { } }
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }

    public void Apply(string title) => Title.Text = title;
}
