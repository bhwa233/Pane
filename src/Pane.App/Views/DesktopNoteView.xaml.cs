using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    Size? GetSize(Guid id);
    void ToggleTopmost(Guid id);
    bool IsTopmost(Guid id);
    void SetReminder(Guid id, DateTimeOffset? due);
    DateTimeOffset? GetReminder(Guid id);
    void DeleteNote(Guid id);
}

/// <summary>桌面便签卡（galaxy NotePad 的 pad/tile 同窗双形态，notes-mvp §2）：与管理器共用
/// NoteSessions 里的同一份草稿，任一处输入另一处立即更新；自动保存由会话中枢统一调度。</summary>
public sealed partial class DesktopNoteView : UserControl, IPanelView, IDisposable
{
    internal const double TileWidth = 256, TileHeight = 170;
    internal const double PadWidth = 260, PadHeight = 320;
    internal const double MinCardWidth = 220, MinCardHeight = 140;
    readonly NoteSessions hub;
    readonly NoteSessions.Session session;
    readonly IDesktopNoteHost host;
    readonly Guid id;
    DesktopNoteWindow? window;
    bool pad;
    bool restoring;
    bool disposed;

    internal DesktopNoteView(NoteSessions hub, Guid id, IDesktopNoteHost host)
    {
        this.hub = hub;
        this.host = host;
        this.id = id;
        InitializeComponent();
        RegisterFoldShortcuts();
        session = hub.Open(id);
        hub.Attach(session);
        RootGrid.RightTapped += RootRightTapped;
        hub.Changed += SessionChanged;
        hub.Saved += SessionSaved;
        hub.SaveFailed += SessionSaveFailed;
        restoring = true;
        try
        {
            Apply(session.Title, session.Body);
        }
        finally { restoring = false; }
    }

    public Size PreferredSize => new(TileWidth, TileHeight);
    public UIElement DragRegion => pad ? TitleArea : TilePane;
    internal Guid NoteId => id;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler? IPanelView.CloseRequested { add { } remove { } }
    internal event EventHandler? UnpinRequested;
    internal event EventHandler<Guid>? FoldRequested;

    /// <summary>折叠入口（notes-mvp §3、§6）：Ctrl+W 走与纸片折叠按钮相同的保存 + 折叠流程。</summary>
    void RegisterFoldShortcuts()
    {
        var fold = new KeyboardAccelerator { Key = VirtualKey.W, Modifiers = VirtualKeyModifiers.Control };
        fold.Invoked += (_, args) => { args.Handled = true; FoldRequested?.Invoke(this, id); };
        KeyboardAccelerators.Add(fold);
        var done = new KeyboardAccelerator { Key = VirtualKey.Escape };
        done.Invoked += (_, args) => { if (pad) { SetPad(false); args.Handled = true; } };
        KeyboardAccelerators.Add(done);
    }

    void FoldClicked(object sender, RoutedEventArgs args) => FoldRequested?.Invoke(this, id);

    /// <summary>快速新建后的落点：光标进正文（notes-mvp §3）。</summary>
    internal void FocusBody()
    {
        if (!pad) { SetPad(true); return; }
        if (PadBody.IsLoaded) PadBody.Focus(FocusState.Programmatic);
        else PadBody.Loaded += FocusOnLoad;

        void FocusOnLoad(object sender, RoutedEventArgs args)
        {
            PadBody.Loaded -= FocusOnLoad;
            if (pad) PadBody.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>折叠前保存失败：保留编辑窗并提示（notes-mvp §6「失败不关窗」）。</summary>
    internal void ReportSaveFailed()
    {
        PadStatus.Text = Strings.Notes_SaveFailed;
        PadStatus.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        hub.Changed -= SessionChanged;
        hub.Saved -= SessionSaved;
        hub.SaveFailed -= SessionSaveFailed;
        hub.Detach(session);
    }

    internal Size AttachWindow(DesktopNoteWindow noteWindow)
    {
        window = noteWindow;
        window.AppWindow.Title = string.IsNullOrWhiteSpace(session.Title) ? Strings.Notes_Untitled : session.Title;
        pad = host.GetSurface(id);
        ApplySurface();
        var topmost = host.IsTopmost(id);
        ApplyTopmost(topmost);
        return host.GetSize(id) ?? (pad ? new(PadWidth, PadHeight) : PreferredSize);
    }

    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }

    /// <summary>共享草稿内容落到界面（贴纸面标题 / 正文，编辑面标题 / 正文）。</summary>
    void Apply(string title, string body)
    {
        if (window is { } noteWindow) noteWindow.AppWindow.Title = string.IsNullOrWhiteSpace(title) ? Strings.Notes_Untitled : title;
        BodyText.Text = body;
        BodyText.Visibility = body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // 内容一致（自己刚保存的）或不在编辑面才回写，避免打断输入、重置光标（notes-mvp §2）。
        if (!pad || (PadTitle.Text == title && PadBody.Text == body))
        {
            restoring = true;
            try
            {
                PadTitle.Text = title;
                PadBody.Text = body;
            }
            finally { restoring = false; }
        }
    }

    /// <summary>总览卡等内容投喂入口。</summary>
    internal void Apply(NoteDocument note) => Apply(note.Title, note.Body);

    // ---------------- 共享会话（notes-mvp §2 实时同步同一份草稿） ----------------

    void SessionChanged(NoteSessions.Session changed, object? source)
    {
        if (disposed || changed.Id != id) return;
        restoring = true;
        try { Apply(changed.Title, changed.Body); }
        finally { restoring = false; }
    }

    void SessionSaved(NoteSessions.Session changed)
    {
        if (disposed || changed.Id != id) return;
        PadStatus.Visibility = Visibility.Collapsed;
    }

    void SessionSaveFailed(NoteSessions.Session changed)
    {
        if (disposed || changed.Id != id) return;
        ReportSaveFailed();
    }

    void PadChanged(object sender, TextChangedEventArgs args)
    {
        if (restoring || !pad || disposed) return;
        hub.Edit(session, PadTitle.Text, PadBody.Text, null, this);
    }

    // ---------------- 形态切换（tile ⇄ pad，galaxy switchSurfaceMode） ----------------

    void ApplySurface()
    {
        if (window is { } noteWindow) noteWindow.AppWindow.IsShownInSwitchers = pad;
        Header.Visibility = pad ? Visibility.Visible : Visibility.Collapsed;
        TilePane.Visibility = pad ? Visibility.Collapsed : Visibility.Visible;
        PadPane.Visibility = pad ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetPad(bool value)
    {
        if (pad == value) return;
        if (!value && (session.Persisted || !session.IsEmpty) && !hub.Flush(session))
        {
            ReportSaveFailed();
            return;
        }
        pad = value;
        host.SaveSurface(id, value);
        restoring = true;
        try { Apply(session.Title, session.Body); }
        finally { restoring = false; }
        ApplySurface();
        window?.Resize(host.GetSize(id) ?? (value ? new(PadWidth, PadHeight) : PreferredSize));
        if (value) FocusBody();
    }

    void CollapseClicked(object sender, RoutedEventArgs args) => SetPad(false);

    // ---------------- 置顶 ----------------

    void ApplyTopmost(bool on)
    {
        TopmostIcon.IsFilled = on;
        var label = on ? Strings.Notes_UnTopmost : Strings.Notes_Topmost;
        AutomationProperties.SetName(TopmostButton, label);
        ToolTipService.SetToolTip(TopmostButton, label);
    }

    void TopmostClicked(object sender, RoutedEventArgs args)
    {
        host.ToggleTopmost(id);
        ApplyTopmost(host.IsTopmost(id));
    }

    // ---------------- 右键菜单 ----------------

    void RootRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        ShowNoteMenu(args.GetPosition(this));
        args.Handled = true;
    }

    internal void ShowNoteMenu(Point position)
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
        menu.ShowAt(this, position);
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
