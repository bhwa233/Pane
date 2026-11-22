using Pane.Localization;
// 翻译窗口逻辑（设计负责人）。照原型 prototype/launcher.html「二级：翻译窗口」：
// 边打边翻（停手 0.6 秒），Ctrl+Enter 立即翻；原文是中文翻成英文，其余翻成「翻译成」（默认跟设置，设置是自动时为中文）；
// 点「翻译成」临时换目标语言（清空后回到自动）；⇄ 把译文放到左边接着翻。
// 引擎（2026-11-22）：设置里选择——AI 模型（默认，流式、按量计费、不参与回退）或免密钥在线引擎
// （有道 / 必应 / 谷歌，失败沿 有道→必应→谷歌 回退，命中即用）；底栏显示当前引擎。
// Esc 只清空原文（不关窗口）；历史页 Esc 返回；Ctrl+W 关。
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Services.Ai;
using Pane.Settings;
using Pane.Services.Translate;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class TranslateView : UserControl, ISecondaryView, IDisposable
{
    static readonly (string Code, string Name)[] Langs = Languages.All;
    static string Idle => Strings.Translate_ResultPlaceholder;

    readonly IAiService ai;
    readonly ISelectionActions output;
    readonly SettingsStore settings;
    readonly DispatcherQueueTimerHolder debounce;
    CancellationTokenSource? running;
    string last = "", observedText = "", target = "zh";
    string? manualTarget;
    bool topmost, settingSource;
    string Engine => settings.Current.Ai.TranslateEngine;

    /// <summary>未配置 AI 时「去设置」。</summary>
    public event EventHandler? OpenSettings;

    public TranslateView(IAiService ai, ISelectionActions output, SettingsStore settings)
    {
        this.ai = ai;
        this.output = output;
        this.settings = settings;
        history = new TranslationHistory(settings.DirectoryPath);
        InitializeComponent();
        Controls.PaneCaret.Attach(Source);
        debounce = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(600), () => _ = Translate(force: false));
        KeyHints.Fill(Hints, [(["Ctrl", "+", "Enter"], Strings.Translate_TranslateNow), (["Esc"], Strings.Common_Clear)], HintLabelProto, KbdProto);
        PreviewKeyDown += OnKeyDown;
        ai.StatusChanged += OnStatusChanged;
        Unloaded += (_, _) => ai.StatusChanged -= OnStatusChanged;
        ShowIdle();
        UpdateModel();
    }

    void OnStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateModel);

    /// <summary>打开（或复用）窗口时宿主调用：带上要翻译的文字，立即翻。</summary>
    public void Open(string? text)
    {
        if (HistoryVisible) ShowHistory(false);
        UpdateModel();
        if (text is null) return;
        CancelPending();
        manualTarget = null;
        SetSource(text);
        _ = Translate(force: true);
    }

    void SetSource(string text)
    {
        settingSource = true;
        try
        {
            Source.Text = text;
            observedText = Source.Text.Trim();
            Source.SelectionStart = Source.Text.Length;
        }
        finally { settingSource = false; }
    }

    // ---------------- ISecondaryView ----------------

    public Size PreferredSize => new(760, 460);
    public Size MinimumSize => new(560, 340);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? TopmostChanged;
    public UIElement? DragRegion => Head;
    internal FrameworkElement WorkspaceCaption => Head;
    internal FrameworkElement[] WorkspaceCaptionControls => [SwapButton, ToButton, CloseButton];
    internal bool IsTopmost => topmost;
    internal void SetWorkspaceMode(bool workspace) => PinButton.Visibility = workspace ? Visibility.Collapsed : Visibility.Visible;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() => Source.Focus(FocusState.Programmatic);
    public void OnHidden() { CancelPending(); }

    // 永久销毁时由宿主调用（GlassPanel.Dispose，055 起能力停用、二级窗口关闭都会销毁视图）。
    // 不能只靠 Unloaded：内容岛直接释放时它不一定来。退订常驻对象的事件、停计时器、取消任务、释放附着的合成视觉
    public void Dispose() { OnHidden(); debounce.Dispose(); ai.StatusChanged -= OnStatusChanged; }

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    void OnPin(object sender, RoutedEventArgs e)
    {
        topmost = !topmost;
        PinIcon.IsFilled = topmost;
        ToolTipService.SetToolTip(PinButton, topmost ? Strings.Common_AlwaysOnTopEnabled : Strings.Common_AlwaysOnTop);
        TopmostChanged?.Invoke(this, topmost);
    }

    // 底栏：AI 引擎显示模型名；在线引擎显示引擎名（命中回退链后也如实显示实际引擎）。
    void UpdateModel() => ModelText.Text = OnlineTranslator.IsOnline(Engine)
        ? OnlineTranslator.NameOf(Engine)
        : ai.Status == AiStatus.NotConfigured || ai.TranslationModelName is null
        ? Strings.Translate_NotConfigured
        : string.Format(Strings.Translate_ModelHint, (object?)(ai.TranslationModelName));

    // ---------------- 翻译 ----------------

    void OnSourceChanged(object sender, TextChangedEventArgs e)
    {
        if (debounce is null) return;
        CountText.Text = string.Format(Strings.Translate_CharacterCount, (object?)(Source.Text.Length));
        var text = Source.Text.Trim();
        if (settingSource || text == observedText) return;
        observedText = text;
        CancelPending();
        ShowIdle();
        last = "";
        if (text.Length == 0)
        {
            manualTarget = null;
            ResetLanguages("");
        }
        else debounce.Restart();
    }

    static string Detect(string s) => Languages.Detect(s);
    static string NameOf(string code) => Languages.NameOf(code);
    string AutoTarget(string text) => Languages.AutoTarget(text, settings.Current.Ai.TranslateTarget);

    void ResetLanguages(string text)
    {
        target = manualTarget ?? AutoTarget(text);
        FromText.Text = text.Length == 0 ? Strings.Translate_AutoDetect : string.Format(Strings.Translate_DetectedLanguage, (object?)(Detect(text)));
        ToText.Text = NameOf(target);
    }

    void CancelPending()
    {
        debounce.Stop();
        var pending = running;
        running = null;
        pending?.Cancel();
        if (messageAction == MessageAction.CancelTranslation) ActionButton.Visibility = Visibility.Collapsed;
    }

    async Task Translate(bool force)
    {
        debounce.Stop();
        var text = Source.Text.Trim();
        if (!force && text == last) return;
        last = text;
        CancelPending();
        ResetLanguages(text);
        if (text.Length == 0) { ShowIdle(); return; }
        ActionButton.Visibility = Visibility.Collapsed;
        if (OnlineTranslator.IsOnline(Engine)) { await TranslateOnlineAsync(text).ConfigureAwait(false); return; }
        if (ai.Status == AiStatus.NotConfigured)
        {
            ShowMessage(Strings.Translate_ConfigureHint, MessageAction.Settings);
            return;
        }
        await TranslateWithAiAsync(text).ConfigureAwait(false);
    }

    // AI 路径：流式输出，按量计费，不参与在线回退（失败如实报错，不静默换引擎）。
    async Task TranslateWithAiAsync(string text)
    {
        using var cts = running = new CancellationTokenSource();
        Out.Text = "";
        Out.Foreground = TextProto.Foreground;
        StateText.Text = Strings.Translate_Translating;
        ShowAction(MessageAction.CancelTranslation);
        try
        {
            await foreach (var piece in ai.RunAsync(new AiRequest("translate", text, target, null), cts.Token))
            {
                if (cts != running) return;
                Out.Text += piece;
            }
            if (cts == running)
            {
                StateText.Text = "";
                OnTranslationCompleted(text, Out.Text);
            }
        }
        catch (OperationCanceledException) { }
        catch (AiSignInRequiredException e) { if (cts == running) ShowMessage(e.Message, MessageAction.Settings); }
        catch (AiCredentialException e) { if (cts == running) ShowMessage(e.Message, MessageAction.Settings); }
        catch (Exception e) { if (cts == running) ShowMessage(App.FailureReason(e) ?? Strings.Translate_Failed, MessageAction.Retry); }
        finally
        {
            if (cts == running)
            {
                running = null;
                if (messageAction == MessageAction.CancelTranslation)
                {
                    if (ActionButton.FocusState == FocusState.Keyboard) Source.Focus(FocusState.Programmatic);
                    ActionButton.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    // 在线路径：整段返回；取消（用户点停止）沿链终止，失败沿 有道→必应→谷歌 回退，命中即用并显示实际引擎。
    async Task TranslateOnlineAsync(string text)
    {
        using var cts = running = new CancellationTokenSource();
        Out.Text = "";
        Out.Foreground = TextProto.Foreground;
        StateText.Text = Strings.Translate_Translating;
        ShowAction(MessageAction.CancelTranslation);
        try
        {
            var result = await OnlineTranslator.TranslateAsync(text, target, Engine, cts.Token).ConfigureAwait(false);
            if (cts != running) return;
            running = null;
            if (!result.Ok)
            {
                ShowMessage(Strings.Translate_Failed, MessageAction.Retry);
                return;
            }
            if (result.DetectedSource is { Length: > 0 } code && code != target)
                FromText.Text = string.Format(Strings.Translate_DetectedLanguage, (object?)(Languages.NameOf(code)));
            Out.Text = result.Text;
            Out.Foreground = TextProto.Foreground;
            // 回退命中了别的引擎时如实标注，和底栏配置的引擎区分。
            StateText.Text = result.Engine != Engine ? OnlineTranslator.NameOf(result.Engine) : "";
            OnTranslationCompleted(text, result.Text);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (cts == running)
            {
                running = null;
                if (messageAction == MessageAction.CancelTranslation)
                {
                    if (ActionButton.FocusState == FocusState.Keyboard) Source.Focus(FocusState.Programmatic);
                    ActionButton.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    // 翻译完成（流式收完或在线整段返回）：按设置记历史。
    void OnTranslationCompleted(string text, string translated)
    {
        if (translated.Length == 0) return;
        if (settings.Current.RememberTranslations)
            RecordHistory(new(text, target, translated));
    }

    void ShowIdle()
    {
        Out.Text = Idle;
        Out.Foreground = IdleProto.Foreground;
        StateText.Text = "";
        ActionButton.IsEnabled = true;
        ActionButton.Visibility = Visibility.Collapsed;
    }

    enum MessageAction { Settings, Retry, CancelTranslation }
    MessageAction messageAction;
    void ShowMessage(string message, MessageAction action)
    {
        Out.Text = message;
        Out.Foreground = IdleProto.Foreground;
        StateText.Text = "";
        ShowAction(action);
    }

    void ShowAction(MessageAction action)
    {
        messageAction = action;
        ActionButton.Content = action switch
        {
            MessageAction.Settings => Strings.Common_GoToSettings,
            MessageAction.CancelTranslation => Strings.Translate_Stop,
            _ => Strings.Common_Retry
        };
        ActionButton.Visibility = Visibility.Visible;
    }

    bool HasResult => Out.Foreground == TextProto.Foreground && Out.Text.Length > 0;

    void OnAction(object sender, RoutedEventArgs e)
    {
        if (messageAction == MessageAction.CancelTranslation)
        {
            if (running == null) return;
            CancelPending();
            if (!HasResult) ShowIdle();
            StateText.Text = Strings.Translate_Cancelled;
            ShowAction(MessageAction.Retry);
            ActionButton.Focus(FocusState.Programmatic);
        }
        else if (messageAction == MessageAction.Settings) OpenSettings?.Invoke(this, EventArgs.Empty);
        else _ = Translate(force: true);
    }

    void OnClear(object sender, RoutedEventArgs e) { Source.Text = ""; Source.Focus(FocusState.Programmatic); }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!HasResult) { StateText.Text = Strings.Translate_NoResult; return; }
        _ = output.CopyAsync(Out.Text, CancellationToken.None);
        StateText.Text = Strings.Translate_Copied;
    }

    // 换目标语言：按列表轮换，跳过和原文相同的语言；只对这段文字生效，清空后回到自动
    void OnTarget(object sender, RoutedEventArgs e)
    {
        int i = Array.FindIndex(Langs, l => l.Code == target);
        var from = Detect(Source.Text);
        for (int k = 1; k <= Langs.Length; k++)
        {
            var next = Langs[(i + k) % Langs.Length];
            if (next.Name != from) { manualTarget = next.Code; break; }
        }
        _ = Translate(force: true);
        Source.Focus(FocusState.Programmatic);
    }

    // 对调：译文放到左边，目标换成原来的语言（原文是中文就回到中文）
    void OnSwap(object sender, RoutedEventArgs e)
    {
        if (!HasResult) return;
        var from = Detect(Source.Text);
        manualTarget = Langs.FirstOrDefault(l => l.Name == from).Code;
        SetSource(Out.Text);
        _ = Translate(force: true);
        Source.Focus(FocusState.Programmatic);
    }

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Down(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.Enter when ctrl: _ = Translate(force: true); break;
            case VirtualKey.Escape when HistoryVisible: ShowHistory(false); HistoryButton.Focus(FocusState.Programmatic); break;
            case VirtualKey.Escape: if (Source.Text.Length > 0) Source.Text = ""; Source.Focus(FocusState.Programmatic); break;   // 不关窗口
            case VirtualKey.W when ctrl: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }
}
