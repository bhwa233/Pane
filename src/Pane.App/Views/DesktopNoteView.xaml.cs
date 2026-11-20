using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Notes;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

/// <summary>桌面便签卡对宿主的诉求（形态 / 尺寸 / 置顶持久化、提醒、删除）。</summary>
internal interface IDesktopNoteHost
{
    void SaveSurface(Guid id, bool pad);
    bool GetSurface(Guid id);
    void SaveSize(Guid id, double width, double height);
    Size? GetSize(Guid id);
    void ToggleTopmost(Guid id);
    bool IsTopmost(Guid id);
    void SetReminder(Guid id, DateTimeOffset? due);
    DateTimeOffset? GetReminder(Guid id);
    void DeleteNote(Guid id);
}

// 桌面便签卡（galaxy NotePad 的 pad/tile 同窗双形态，040）：贴纸面只读展示，编辑面就地编辑并自动保存，
// 玻璃外观不变。置顶 / 拖角缩放 / 右键菜单都挂在这张卡上；旧「打开主窗口编辑」路径已由双形态替代。
public sealed partial class DesktopNoteView : UserControl, IPanelView
{
    internal const double TileWidth = 256, TileHeight = 170;
    internal const double PadWidth = 260, PadHeight = 320;
    internal const double MinCardWidth = 220, MinCardHeight = 140;
    readonly NoteStore store;
    readonly IDesktopNoteHost host;
    Guid id;
    Guid previewImage;
    GlassPanel? panel;
    bool pad;
    bool restoring;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? autosave;

    internal DesktopNoteView(NoteDocument note, NoteStore store, IDesktopNoteHost host)
    {
        this.store = store;
        this.host = host;
        InitializeComponent();
        id = note.Id;
        RootGrid.RightTapped += RootRightTapped;
        Apply(note);
    }

    public Size PreferredSize => new(TileWidth, TileHeight);
    public UIElement DragRegion => Header;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler? IPanelView.CloseRequested { add { } remove { } }
    internal event EventHandler? UnpinRequested;

    internal void AttachPanel(GlassPanel cardPanel)
    {
        panel = cardPanel;
        // 应用持久化：形态 → 尺寸 → 置顶。
        pad = host.GetSurface(id);
        var size = host.GetSize(id);
        if (size is { } current && current.Width > 0) panel.RequestSize(current);
        else if (pad) panel.RequestSize(new(PadWidth, PadHeight));
        ApplySurface();
        ApplyTopmost(host.IsTopmost(id));
    }

    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }

    internal void Apply(NoteDocument note)
    {
        if (id != note.Id) return;
        var title = string.IsNullOrWhiteSpace(note.Title) ? Strings.Notes_Untitled : note.Title;
        TitleText.Text = title;
        BodyText.Text = note.Body;
        BodyText.Visibility = note.Body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // 编辑面正有本地未落盘的改动（600ms 节流窗口内）时，Changed 事件回灌会打断输入、重置光标；
        // 内容一致（自己刚保存的）或不在编辑面才回写，其余等下一次保存后的刷新。
        if (!pad || (PadTitle.Text == note.Title && PadBody.Text == note.Body))
        {
            restoring = true;
            try
            {
                PadTitle.Text = note.Title;
                PadBody.Text = note.Body;
            }
            finally { restoring = false; }
        }
        var markdown = note.EditorMode == 1;
        PadFormatBold.Visibility = PadFormatHeading.Visibility = PadFormatList.Visibility =
            markdown ? Visibility.Visible : Visibility.Collapsed;
        PadStatus.Text = "";
        var image = note.Images?.FirstOrDefault() ?? Guid.Empty;
        if (image == previewImage) return;
        previewImage = image;
        var path = image == Guid.Empty ? null : store.FindImage(note.Id, image);
        ImagePreview.Source = path == null ? null : new BitmapImage
            { DecodePixelWidth = 320, DecodePixelType = DecodePixelType.Physical, UriSource = new Uri(path) };
        ImagePreview.Visibility = path == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- 形态切换（tile ⇄ pad，galaxy switchSurfaceMode） ----------------

    void ApplySurface()
    {
        TilePane.Visibility = pad ? Visibility.Collapsed : Visibility.Visible;
        PadPane.Visibility = pad ? Visibility.Visible : Visibility.Collapsed;
        var padLabel = pad ? Strings.Notes_CollapsePad : Strings.Notes_SwitchToPad;
        AutomationProperties.SetName(PadButton, padLabel);
        ToolTipService.SetToolTip(PadButton, padLabel);
        PadIcon.Glyph = pad ? "chev-d" : "pencil";
    }

    void SetPad(bool value)
    {
        if (pad == value) return;
        pad = value;
        host.SaveSurface(id, value);
        ApplySurface();
        // 同窗双形态：请求目标尺寸，GlassPanel 的弹簧动画原地展开 / 收回（galaxy animateCurrentWindowBounds 对齐）。
        var persisted = host.GetSize(id);
        if (value)
        {
            var size = persisted is { } current ? current : new Size(PadWidth, PadHeight);
            panel?.RequestSize(size);
            PadBody.Focus(FocusState.Programmatic);
        }
        else
        {
            SaveCardNow();
            var size = persisted is { } current ? current : new Size(TileWidth, TileHeight);
            panel?.RequestSize(size);
        }
    }

    void PadClicked(object sender, RoutedEventArgs args) => SetPad(!pad);
    void CollapseClicked(object sender, RoutedEventArgs args) => SetPad(false);

    // ---------------- 编辑面自动保存 ----------------

    void PadChanged(object sender, TextChangedEventArgs args) => SchedulePadSave();

    void SchedulePadSave()
    {
        if (restoring || !pad) return;
        autosave ??= DispatcherQueue.CreateTimer();
        autosave.Interval = TimeSpan.FromMilliseconds(600);
        autosave.IsRepeating = false;
        autosave.Tick -= AutosaveTick;
        autosave.Tick += AutosaveTick;
        PadStatus.Text = Strings.Common_NotSaved;
        autosave.Stop();
        autosave.Start();
    }

    void AutosaveTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object? args) => SaveCardNow();

    void SaveCardNow()
    {
        autosave?.Stop();
        try
        {
            store.SaveCardEdit(id, PadTitle.Text, PadBody.Text);
            PadStatus.Text = Strings.Notes_Saved;
        }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { PadStatus.Text = Strings.Notes_SaveFailed; }
    }

    // ---------------- 精简格式按钮（仅 Markdown 便签） ----------------

    void CardFormatBold(object sender, RoutedEventArgs args) => WrapSelection("**", "**", "粗体文本");
    void CardFormatHeading(object sender, RoutedEventArgs args)
    {
        var box = PadBody;
        var caret = box.SelectionStart;
        var lineStart = box.Text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
        var current = 0;
        while (current < 5 && lineStart + current < box.Text.Length && box.Text[lineStart + current] == '#') current++;
        if (current > 0 && lineStart + current < box.Text.Length && box.Text[lineStart + current] == ' ')
        {
            var next = current < 5 ? new string('#', current + 1) : "#";
            restoring = true;
            try
            {
                box.Text = box.Text[..lineStart] + next + " " + box.Text[(lineStart + current + 1)..];
                box.Select(lineStart + next.Length + 1, 0);
            }
            finally { restoring = false; }
            SchedulePadSave();
            return;
        }
        restoring = true;
        try
        {
            box.Text = box.Text[..lineStart] + "## " + box.Text[lineStart..];
            box.Select(lineStart + 3, 0);
        }
        finally { restoring = false; }
        SchedulePadSave();
    }

    void CardFormatList(object sender, RoutedEventArgs args)
    {
        var box = PadBody;
        var caret = box.SelectionStart;
        var lineStart = box.Text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
        var lineEnd = box.Text.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = box.Text.Length;
        var line = box.Text[lineStart..lineEnd];
        if (line.StartsWith("- ", StringComparison.Ordinal)) return;
        restoring = true;
        try
        {
            box.Text = box.Text[..lineStart] + "- " + line + box.Text[lineEnd..];
            box.Select(lineStart + 2, 0);
        }
        finally { restoring = false; }
        SchedulePadSave();
    }

    void WrapSelection(string before, string after, string sample)
    {
        var box = PadBody;
        var start = box.SelectionStart;
        var selected = box.Text.Substring(start, box.SelectionLength);
        var text = selected.Length > 0 ? selected : sample;
        restoring = true;
        try
        {
            box.Text = box.Text[..start] + before + text + after + box.Text[(start + box.SelectionLength)..];
            box.Select(start + before.Length, text.Length);
        }
        finally { restoring = false; }
        SchedulePadSave();
    }

    // ---------------- 置顶 ----------------

    void ApplyTopmost(bool on)
    {
        TopmostIcon.Glyph = on ? "pin-off" : "pin";
        var label = on ? Strings.Notes_UnTopmost : Strings.Notes_Topmost;
        AutomationProperties.SetName(TopmostButton, label);
        ToolTipService.SetToolTip(TopmostButton, label);
    }

    void TopmostClicked(object sender, RoutedEventArgs args)
    {
        host.ToggleTopmost(id);
        var on = host.IsTopmost(id);
        panel?.SetTopmost(on);
        ApplyTopmost(on);
    }

    // ---------------- 拖角缩放 ----------------

    bool dragging;
    double gripOriginX, gripOriginY, gripStartWidth, gripStartHeight;

    void GripPressed(object sender, PointerRoutedEventArgs args)
    {
        if (panel == null) return;
        dragging = true;
        var point = args.GetCurrentPoint(null);
        gripOriginX = point.Position.X;
        gripOriginY = point.Position.Y;
        gripStartWidth = panel.Width;
        gripStartHeight = panel.Height;
        (sender as UIElement)?.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    void GripMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!dragging || panel == null) return;
        var point = args.GetCurrentPoint(null);
        // 手拖期间直接请求目标尺寸，弹簧跟进；松手时按最终尺寸落盘。
        var width = Math.Clamp(gripStartWidth + point.Position.X - gripOriginX, MinCardWidth, 720);
        var height = Math.Clamp(gripStartHeight + point.Position.Y - gripOriginY, MinCardHeight, 900);
        panel.RequestSize(new(width, height));
        args.Handled = true;
    }

    void GripReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!dragging) return;
        dragging = false;
        (sender as UIElement)?.ReleasePointerCapture(args.Pointer);
        if (panel != null) host.SaveSize(id, panel.Width, panel.Height);
        args.Handled = true;
    }

    // ---------------- 右键菜单 ----------------

    void RootRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        var surface = new MenuFlyoutItem { Text = pad ? Strings.Notes_CollapsePad : Strings.Notes_SwitchToPad };
        surface.Click += (_, _) => SetPad(!pad);
        menu.Items.Add(surface);
        var topmost = new MenuFlyoutItem { Text = host.IsTopmost(id) ? Strings.Notes_UnTopmost : Strings.Notes_Topmost };
        topmost.Click += (_, _) => TopmostClicked(this, new RoutedEventArgs());
        menu.Items.Add(topmost);
        menu.Items.Add(ReminderMenu());
        var danger = new MenuFlyoutSubItem { Text = Strings.Notes_DeleteCard };
        var confirm = new MenuFlyoutItem { Text = Strings.Notes_DeleteRowConfirm };
        confirm.Click += (_, _) => host.DeleteNote(id);
        danger.Items.Add(confirm);
        menu.Items.Add(danger);
        menu.ShowAt(this, args.GetPosition(this));
        args.Handled = true;
    }

    MenuFlyoutSubItem ReminderMenu()
    {
        var menu = new MenuFlyoutSubItem { Text = Strings.Notes_Reminder };
        void AddPreset(string text, Func<DateTimeOffset> due)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => host.SetReminder(id, due());
            menu.Items.Add(item);
        }
        AddPreset(Strings.Notes_ReminderTenMinutes, () => DateTimeOffset.UtcNow.AddMinutes(10));
        AddPreset(Strings.Notes_ReminderThirtyMinutes, () => DateTimeOffset.UtcNow.AddMinutes(30));
        AddPreset(Strings.Notes_ReminderOneHour, () => DateTimeOffset.UtcNow.AddHours(1));
        AddPreset(Strings.Notes_ReminderTomorrowMorning, () =>
            new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9)).ToUniversalTime());
        var cancel = new MenuFlyoutItem { Text = Strings.Notes_ReminderCancel };
        cancel.Click += (_, _) => host.SetReminder(id, null);
        menu.Items.Add(cancel);
        return menu;
    }

    void UnpinClicked(object sender, RoutedEventArgs args) => UnpinRequested?.Invoke(this, EventArgs.Empty);
}
