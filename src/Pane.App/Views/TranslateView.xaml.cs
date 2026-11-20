using Pane.Localization;
// 翻译窗口逻辑（设计负责人）。照原型 prototype/launcher.html「二级：翻译窗口」：
// 边打边翻（停手 0.6 秒），Ctrl+Enter 立即翻；原文是中文翻成英文，其余翻成「翻译成」（默认跟设置，设置是自动时为中文）；
// 点「翻译成」临时换目标语言（清空后回到自动）；⇄ 把译文放到左边接着翻。翻译用设置里单独指定的快模型。
// Esc 只清空原文（不关窗口）；Ctrl+W 关。
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Services.Ai;
using Pane.Services.Notes;
using Pane.Settings;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class TranslateView : UserControl, ISecondaryView, IDisposable
{
    static readonly (string Code, string Name)[] Langs = Languages.All;
    static string Idle => Strings.Translate_ResultPlaceholder;
    const string TranslateMode = "translate", DictionaryMode = "dictionary";

    readonly IAiService ai;
    readonly ISelectionActions output;
    readonly Func<string, string, bool> saveNote;
    readonly DispatcherQueueTimerHolder debounce;
    readonly PaneSegmented mode;
    CancellationTokenSource? running;
    string last = "", observedText = "", target = "zh";
    string? manualTarget;
    bool topmost, settingSource;
    bool IsDictionary => mode.Value == DictionaryMode;

    /// <summary>未配置 AI 时「去设置」。</summary>
    public event EventHandler? OpenSettings;

    public TranslateView(IAiService ai, ISelectionActions output, SettingsStore settings, Func<string, string, bool> saveNote)
    {
        this.ai = ai;
        this.output = output;
        this.saveNote = saveNote;
        historySettings = settings;
        dictionaryHistory = new TranslationHistory(settings.DirectoryPath, dictionary: true);
        translationHistory = new TranslationHistory(settings.DirectoryPath, dictionary: false);
        InitializeComponent();
        Controls.PaneCaret.Attach(Source);
        debounce = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(600), () => _ = RunCurrent(force: false));
        selectionLookupDebounce = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(250), () => _ = LookupSelectionAsync());
        mode = new PaneSegmented([(TranslateMode, Strings.Common_Translate), (DictionaryMode, Strings.Translate_Dictionary)], TranslateMode,
            ModeStrongProto, ModeDimProto, ModeFillProto.Background, ModeFillProto.BorderBrush);
        AutomationProperties.SetName(mode, Strings.Translate_Mode);
        mode.Changed += (_, _) =>
        {
            ApplyMode();
            if (IsDictionary && !HistoryVisible) _ = Lookup(force: true);
        };
        ModeHost.Content = mode;
        PreviewKeyDown += OnKeyDown;
        ai.StatusChanged += OnStatusChanged;
        Unloaded += (_, _) => ai.StatusChanged -= OnStatusChanged;
        SelectionDictionaryToggle.IsChecked = settings.Current.LookupTranslationSelection;
        ApplyMode();
    }

    void OnStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateModel);

    /// <summary>打开（或复用）窗口时宿主调用：带上要翻译的文字，立即翻。</summary>
    public void Open(string? text, bool dictionary = false)
    {
        if (HistoryVisible) ShowHistory(false);
        if (text is null && IsDictionary == dictionary) { UpdateModel(); return; }
        CancelPending();
        mode.SetValue(dictionary ? DictionaryMode : TranslateMode);
        if (text != null) SetSource(text);
        ApplyMode();
        if (text != null || dictionary) _ = RunCurrent(force: true);
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

    void ApplyMode()
    {
        CancelPending();
        ClearSelectionLookup();
        RefreshSelectionDictionaryVisibility();
        manualTarget = null;
        last = observedText = Source.Text.Trim();
        ResetLanguages(last);
        LanguagesBar.Visibility = IsDictionary || HistoryVisible ? Visibility.Collapsed : Visibility.Visible;
        Source.PlaceholderText = IsDictionary ? Strings.Translate_DictionaryInput : Strings.Translate_InputPlaceholder;
        CopyButton.Content = IsDictionary ? Strings.Common_Copy : Strings.SuperPanel_CopyTranslation;
        ShowIdle();
        UpdateModel();
        RefreshHints();
        if (HistoryVisible) RefreshHistory();
    }

    // ---------------- ISecondaryView ----------------

    public Size PreferredSize => new(760, 460);
    public Size MinimumSize => new(560, 340);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? TopmostChanged;
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() => Source.Focus(FocusState.Programmatic);
    public void OnHidden() { CancelPending(); ClearSelectionLookup(); }

    // 永久销毁时由宿主调用（GlassPanel.Dispose，055 起能力停用、二级窗口关闭都会销毁视图）。
    // 不能只靠 Unloaded：内容岛直接释放时它不一定来。退订常驻对象的事件、停计时器、取消任务、释放附着的合成视觉
    public void Dispose() { OnHidden(); debounce.Dispose(); selectionLookupDebounce.Dispose(); ai.StatusChanged -= OnStatusChanged; }

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    void OnPin(object sender, RoutedEventArgs e)
    {
        topmost = !topmost;
        PinIcon.IsFilled = topmost;
        ToolTipService.SetToolTip(PinButton, topmost ? Strings.Common_AlwaysOnTopEnabled : Strings.Common_AlwaysOnTop);
        TopmostChanged?.Invoke(this, topmost);
    }

    void UpdateModel() => ModelText.Text = IsDictionary ? Strings.Translate_DictionarySource
        : ai.Status == AiStatus.NotConfigured || ai.TranslationModelName is null
        ? Strings.Translate_NotConfigured
        : string.Format(Strings.Translate_ModelHint, (object?)(ai.TranslationModelName));

    // ---------------- 翻译 ----------------

    void OnSourceChanged(object sender, TextChangedEventArgs e)
    {
        if (debounce is null) return;
        if (selectionLookupOwner == Source) ClearSelectionLookup();
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
            if (IsDictionary && !HistoryVisible) _ = Lookup(force: true);
        }
        else debounce.Restart();
    }

    static string Detect(string s) => Languages.Detect(s);
    static string NameOf(string code) => Languages.NameOf(code);
    string AutoTarget(string text) => Languages.AutoTarget(text, ((App)Application.Current).Store.Current.Ai.TranslateTarget);

    void ResetLanguages(string text)
    {
        if (IsDictionary) return;
        target = manualTarget ?? AutoTarget(text);
        FromText.Text = text.Length == 0 ? Strings.Translate_AutoDetect : string.Format(Strings.Translate_DetectedLanguage, Detect(text));
        ToText.Text = NameOf(target);
    }

    void RefreshHints() => KeyHints.Fill(Hints,
        [(["Ctrl", "+", "Enter"], IsDictionary ? Strings.Translate_Dictionary : Strings.Translate_TranslateNow), (["Esc"], Strings.Common_Clear)],
        HintLabelProto, KbdProto);

    Task RunCurrent(bool force) => HistoryVisible ? Task.CompletedTask : IsDictionary ? Lookup(force) : Translate(force);

    void CancelPending()
    {
        debounce.Stop();
        var pending = running;
        running = null;
        pending?.Cancel();
        if (messageAction == MessageAction.CancelTranslation) ActionButton.Visibility = Visibility.Collapsed;
        if (messageAction == MessageAction.InstallDictionary) ActionButton.IsEnabled = true;
    }

    async Task Lookup(bool force)
    {
        var text = Source.Text.Trim();
        if (!force && text == last) return;
        CancelPending();
        last = text;
        ShowIdle();
        var dictionaryPath = OfflineDictionary.FindPath(historySettings.DirectoryPath, AppContext.BaseDirectory);
        if (dictionaryPath == null)
        {
            ShowMessage(Strings.Translate_DictionaryUnavailable, MessageAction.InstallDictionary);
            return;
        }
        if (text.Length == 0) return;

        using var cts = running = new CancellationTokenSource();
        try
        {
            var entries = await Task.Run(() => OfflineDictionary.Lookup(dictionaryPath, text, cts.Token), cts.Token);
            if (cts != running) return;
            if (entries.Count == 0) { Out.Text = Strings.Translate_DictionaryNoMatch; return; }
            Out.Foreground = TextProto.Foreground;
            Out.Text = FormatDictionaryEntries(entries);
            SaveNoteButton.IsEnabled = !string.IsNullOrWhiteSpace(Out.Text);
            OutScroller.ChangeView(null, 0, null, true);
            RecordHistory(new(text));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (cts == running) ShowMessage(Strings.Translate_DictionaryUnavailable, MessageAction.InstallDictionary);
        }
        finally { if (cts == running) running = null; }
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
        if (ai.Status == AiStatus.NotConfigured)
        {
            ShowMessage(Strings.Translate_ConfigureHint, MessageAction.Settings);
            return;
        }

        using var cts = running = new CancellationTokenSource();
        SaveNoteButton.IsEnabled = false;
        if (selectionLookupOwner == Out) ClearSelectionLookup();
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
                SaveNoteButton.IsEnabled = !string.IsNullOrWhiteSpace(Out.Text);
                if (historySettings.Current.RememberTranslations && SaveNoteButton.IsEnabled)
                    RecordHistory(new(text, target, Out.Text));
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

    void ShowIdle()
    {
        if (selectionLookupOwner == Out) ClearSelectionLookup();
        SaveNoteButton.IsEnabled = false;
        Out.Text = IsDictionary ? Strings.Translate_DictionaryPlaceholder : Idle;
        Out.Foreground = IdleProto.Foreground;
        RefreshLookupResult();
        StateText.Text = "";
        ActionButton.IsEnabled = true;
        ActionButton.Visibility = Visibility.Collapsed;
    }

    enum MessageAction { Settings, Retry, InstallDictionary, LookupDictionary, CancelTranslation }
    MessageAction messageAction;
    void ShowMessage(string message, MessageAction action)
    {
        if (selectionLookupOwner == Out) ClearSelectionLookup();
        SaveNoteButton.IsEnabled = false;
        Out.Text = message;
        Out.Foreground = IdleProto.Foreground;
        RefreshLookupResult();
        StateText.Text = "";
        ShowAction(action);
    }

    void ShowAction(MessageAction action)
    {
        messageAction = action;
        ActionButton.Content = action switch
        {
            MessageAction.Settings => Strings.Common_GoToSettings,
            MessageAction.InstallDictionary => Strings.Translate_InstallDictionary,
            MessageAction.LookupDictionary => Strings.Translate_Dictionary,
            MessageAction.CancelTranslation => Strings.Translate_Stop,
            _ => Strings.Common_Retry
        };
        ActionButton.Visibility = Visibility.Visible;
    }

    bool HasResult => Out.Foreground == TextProto.Foreground && Out.Text.Length > 0;

    void OnSaveNote(object sender, RoutedEventArgs e)
    {
        if (!SaveNoteButton.IsEnabled) return;
        var title = Source.Text.Trim();
        var newline = title.IndexOfAny(['\r', '\n']);
        if (newline >= 0) title = title[..newline].TrimEnd();
        if (title.Length > NoteStore.TitleLimit)
            title = title[..(char.IsHighSurrogate(title[NoteStore.TitleLimit - 1]) ? NoteStore.TitleLimit - 1 : NoteStore.TitleLimit)];
        try { StateText.Text = saveNote(title, Out.Text) ? Strings.Notes_ResultSaved : Strings.Notes_ResultModuleDisabled; }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { StateText.Text = Strings.Notes_ResultSaveFailed; }
    }

    void OnResultSelectionChanged(object sender, RoutedEventArgs e)
    {
        RefreshLookupResult();
        if (mode != null) QueueSelectionLookup(Out, HasResult ? Out.SelectedText : null);
    }

    void RefreshLookupResult()
    {
        if (LookupResultButton != null)
            LookupResultButton.IsEnabled = HasResult && Out.SelectedText?.Trim().Length is > 0 and <= 512;
    }

    void OnLookupResult(object sender, RoutedEventArgs e)
    {
        var selected = Out.SelectedText?.Trim() ?? "";
        if (!HasResult || selected.Length is 0 or > 512) return;
        Open(selected, dictionary: true);
        Source.Focus(FocusState.Programmatic);
    }

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
        else if (messageAction == MessageAction.InstallDictionary) _ = InstallDictionaryAsync();
        else _ = RunCurrent(force: true);
    }

    void OnClear(object sender, RoutedEventArgs e) { Source.Text = ""; Source.Focus(FocusState.Programmatic); }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!HasResult) { StateText.Text = IsDictionary ? Strings.Translate_DictionaryNoMatch : Strings.Translate_NoResult; return; }
        _ = output.CopyAsync(Out.Text, CancellationToken.None);
        StateText.Text = IsDictionary ? Strings.Translate_DictionaryCopied : Strings.Translate_Copied;
    }

    // 换目标语言：按列表轮换，跳过和原文相同的语言；只对这段文字生效，清空后回到自动
    void OnTarget(object sender, RoutedEventArgs e)
    {
        if (IsDictionary) return;
        int i = Array.FindIndex(Langs, l => l.Code == target);
        var from = Detect(Source.Text);
        for (int k = 1; k <= Langs.Length; k++)
        {
            var next = Langs[(i + k) % Langs.Length];
            if (next.Name != from) { manualTarget = next.Code; break; }
        }
        _ = RunCurrent(force: true);
        Source.Focus(FocusState.Programmatic);
    }

    // 对调：译文放到左边，目标换成原来的语言（原文是中文就回到中文）
    void OnSwap(object sender, RoutedEventArgs e)
    {
        if (IsDictionary || !HasResult) return;
        var from = Detect(Source.Text);
        manualTarget = Langs.FirstOrDefault(l => l.Name == from).Code;
        SetSource(Out.Text);
        _ = RunCurrent(force: true);
        Source.Focus(FocusState.Programmatic);
    }

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Down(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.Enter when ctrl: _ = RunCurrent(force: true); break;
            case VirtualKey.Escape when HistoryVisible: ShowHistory(false); HistoryButton.Focus(FocusState.Programmatic); break;
            case VirtualKey.Escape: if (Source.Text.Length > 0) Source.Text = ""; Source.Focus(FocusState.Programmatic); break;   // 不关窗口
            case VirtualKey.W when ctrl: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }
}
