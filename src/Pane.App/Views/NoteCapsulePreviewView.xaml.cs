using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Hosting;
using Pane.Localization;
using Windows.Foundation;

namespace Pane.Views;

/// <summary>胶囊悬停预览（notes-mvp §7）：只读纯文本，不取得键盘焦点；可移入滚动。
/// 与胶囊及二者间的窄通道组成连续悬停区域，由协调层保证位置与收回时机。</summary>
public sealed partial class NoteCapsulePreviewView : UserControl, IPanelView
{
    const double PreviewWidth = 300, PreviewHeight = 220;
    Guid id;

    public NoteCapsulePreviewView()
    {
        InitializeComponent();
        // 走廊悬停（notes-mvp §7）：预览卡自身被悬停时向协调层续命，离开时进入收回宽限。
        PointerEntered += (_, _) => HoverChanged?.Invoke(true);
        PointerExited += (_, _) => HoverChanged?.Invoke(false);
    }

    /// <summary>预览卡自身的悬停状态变化（胶囊 → 预览卡走廊的两端之一）。</summary>
    public event Action<bool>? HoverChanged;

    public Size PreferredSize => new(PreviewWidth, PreviewHeight);
    public UIElement DragRegion => this;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler? IPanelView.CloseRequested { add { } remove { } }
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }

    public Guid NoteId => id;

    /// <summary>展示纯文本内容（截断显示，避免超长正文一次性排版）。</summary>
    public void Apply(Guid noteId, string title, string body)
    {
        id = noteId;
        PreviewTitle.Text = string.IsNullOrWhiteSpace(title) ? Strings.Notes_Untitled : title;
        PreviewBody.Text = body.Length > 4000 ? body[..4000] + "…" : body;
        PreviewScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>滚回顶部（预览重新打开时）。</summary>
    public void ResetScroll() => PreviewScroller.ChangeView(null, 0, null, disableAnimation: true);
}
