using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Pane.Localization;
using Pane.Services.Notes;
using Pane.Views.Controls;
using Windows.System;

namespace Pane.Views;

// 编辑工具行（galaxy 对齐，040）：格式按钮（applyFormat 语义直译）、编辑/分栏视图、
// 每条便签独立的纯文本/Markdown 模式、字数统计和撤销重做。分栏的预览复用 MarkdownRender；
// 撤销栈按输入节流快照，独立于系统输入框自带的 Ctrl+Z。
public sealed partial class NotesView
{
    const string MarkdownMode = "markdown", TextMode = "text";
    bool splitView;
    double splitRatio = 0.5;
    bool draggingDivider;
    double dividerOrigin;
    double dividerStartRatio;
    ScrollViewer? bodyScroller;
    ScrollViewer? splitScroller;
    // 撤销栈：节流快照（标题+正文），栈顶是当前内容之前的状态。
    readonly List<(string Title, string Body)> undoStack = [];
    readonly List<(string Title, string Body)> redoStack = [];
    DateTimeOffset lastSnapshot = DateTimeOffset.MinValue;
    bool applyingHistory;
    const int UndoLimit = 40;
    static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(500);

    bool MarkdownEditing => store.Draft is { } draft && draft.EditorMode == 1;

    // ---------------- 编辑模式（纯文本 / Markdown） ----------------

    void MarkdownModeClicked(object sender, RoutedEventArgs args)
    {
        if (store.Draft is not { } draft) return;
        try { store.SetEditorMode(draft.EditorMode == 1 ? 0 : 1); }
        catch (Exception error) when (NoteStore.IsStorageError(error)) { Status.Text = Strings.Settings_SaveFailed; return; }
        PresentDraft();
    }

    void RefreshToolbar()
    {
        var markdown = MarkdownEditing;
        EditorToolbar.Visibility = ready && historyNoteId == null && !ViewingRecycle ? Visibility.Visible : Visibility.Collapsed;
        FormatButtons.Visibility = markdown ? Visibility.Visible : Visibility.Collapsed;
        MarkdownModeButton.Content = markdown ? Strings.Notes_TextMode : Strings.Notes_MarkdownMode;
        var label = markdown ? Strings.Notes_TextMode : Strings.Notes_MarkdownMode;
        AutomationProperties.SetName(MarkdownModeButton, label);
        ToolTipService.SetToolTip(MarkdownModeButton, label);
        // 纯文本没有 Markdown 预览/分栏；退出时收回分栏。
        if (!markdown && splitView) SetSplit(false);
        SplitModeButton.IsEnabled = markdown && !previewing;
        SplitModeButton.Opacity = markdown && !previewing ? 1 : 0.45;
        EditModeButton.IsEnabled = !previewing;
        UndoButton.IsEnabled = undoStack.Count > 0;
        RedoButton.IsEnabled = redoStack.Count > 0;
        RefreshCharCount();
    }

    void RefreshCharCount()
    {
        CharCount.Text = store.Draft is { Body.Length: > 0 } draft
            ? string.Format(Strings.Notes_CharCount, (object?)draft.Body.Length.ToString("N0")) : "";
    }

    // ---------------- 视图：编辑 / 分栏 ----------------

    void EditViewClicked(object sender, RoutedEventArgs args)
    {
        if (previewing) SetPreview(false);
        if (splitView) SetSplit(false);
        BodyBox.Focus(FocusState.Programmatic);
    }

    void SplitViewClicked(object sender, RoutedEventArgs args)
    {
        if (!MarkdownEditing || previewing) return;
        SetSplit(!splitView);
    }

    void SetSplit(bool visible)
    {
        splitView = visible;
        if (visible && !BuildSplitPreview()) { splitView = false; return; }
        SplitDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SplitPreview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SplitDividerColumn.Width = visible ? new GridLength(8) : new GridLength(0);
        BodyColumn.Width = new GridLength(visible ? splitRatio * 100 : 100, GridUnitType.Star);
        SplitColumn.Width = new GridLength(visible ? (1 - splitRatio) * 100 : 0, GridUnitType.Star);
        if (visible)
        {
            if (bodyScroller == null) bodyScroller = FindScroller(BodyBox);
            if (splitScroller == null) splitScroller = FindScroller(SplitPreview);
            SyncSplitScroll();
        }
        RefreshToolbar();
    }

    bool BuildSplitPreview()
    {
        if (store.Draft is not { Body.Length: > 0 } draft) { SplitBlocks.Children.Clear(); return true; }
        if (draft.Body.Length > 40_000 || draft.Body.Count(character => character == '\n') > 600)
        {
            Status.Text = Strings.Notes_PreviewTooLong;
            return false;
        }
        SplitBlocks.Children.Clear();
        var style = SplitStyle();
        foreach (var block in MarkdownRender.Build(draft.Body, style, null, out _, (row, task) => AddTaskReminderButton(row, draft.Id, task)))
            SplitBlocks.Children.Add(block);
        return true;
    }

    MarkdownStyle SplitStyle() => new()
    {
        Font = BodyBox.FontFamily, Mono = MonoProto.FontFamily, MathFont = MathProto.FontFamily,
        Size = BodyBox.FontSize, LineHeight = 24,
        Text1 = BodyBox.Foreground, Text2 = MonoProto.Foreground, Text3 = Text3Proto.Foreground,
        Stroke = CodeProto.BorderBrush, CodeFill = CodeProto.Background, Accent = (SolidColorBrush)MathProto.Foreground,
    };

    void SyncSplitScroll()
    {
        if (!splitView || bodyScroller is not { } body || splitScroller is not { } split) return;
        var bodyRange = body.ExtentHeight - body.ViewportHeight;
        var splitRange = split.ExtentHeight - split.ViewportHeight;
        if (bodyRange <= 0 || splitRange <= 0) return;
        split.ChangeView(null, body.VerticalOffset / bodyRange * splitRange, null, disableAnimation: true);
    }

    void BodyScrollerChanged(object? sender, ScrollViewerViewChangedEventArgs args) => SyncSplitScroll();

    void DividerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!splitView) return;
        draggingDivider = true;
        dividerOrigin = args.GetCurrentPoint(null).Position.X;
        dividerStartRatio = splitRatio;
        (sender as UIElement)?.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    void DividerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!draggingDivider || sender is not UIElement element) return;
        var delta = args.GetCurrentPoint(null).Position.X - dividerOrigin;
        var width = (element.XamlRoot is { } root ? root.Size.Width : 0) - 28;
        if (width <= 0) return;
        // 编辑窗占屏宽的比例；拖动方向 = delta 越大左栏越宽。
        splitRatio = Math.Clamp(dividerStartRatio + delta / width, 0.25, 0.75);
        BodyColumn.Width = new GridLength(splitRatio * 100, GridUnitType.Star);
        SplitColumn.Width = new GridLength((1 - splitRatio) * 100, GridUnitType.Star);
        args.Handled = true;
    }

    void DividerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!draggingDivider) return;
        draggingDivider = false;
        (sender as UIElement)?.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    static ScrollViewer? FindScroller(FrameworkElement element)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i) is not FrameworkElement child) continue;
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    // ---------------- 格式按钮（galaxy applyFormat 语义直译） ----------------

    void FormatBoldClicked(object sender, RoutedEventArgs args) => WrapSelection("**", "**", Strings.Notes_FormatBold, "粗体文本");
    void FormatItalicClicked(object sender, RoutedEventArgs args) => WrapSelection("*", "*", Strings.Notes_FormatItalic, "斜体文本");

    void FormatHrClicked(object sender, RoutedEventArgs args)
    {
        var box = BodyBox;
        var before = box.Text[..box.SelectionStart];
        var after = box.Text[(box.SelectionStart + box.SelectionLength)..];
        var newlineBefore = before.Length == 0 || before.EndsWith('\n') ? "" : "\n";
        var newlineAfter = after.Length == 0 || after.StartsWith('\n') ? "" : "\n";
        var at = box.SelectionStart + newlineBefore.Length + 3;
        ApplyEditedText(before + newlineBefore + "---" + newlineAfter + after, at, at);
    }

    void FormatListClicked(object sender, RoutedEventArgs args) => PrefixLines("- ", Strings.Notes_FormatList);
    void FormatOrderedListClicked(object sender, RoutedEventArgs args) => PrefixNumberedLines(Strings.Notes_FormatOrderedList);
    void FormatCodeClicked(object sender, RoutedEventArgs args) => WrapLines("```\n", "\n```", "`", "`", Strings.Notes_FormatCode, "代码");
    void FormatQuoteClicked(object sender, RoutedEventArgs args) => PrefixLines("> ", Strings.Notes_FormatQuote);

    void FormatHeadingClicked(object sender, RoutedEventArgs args)
    {
        var box = BodyBox;
        var caret = box.SelectionStart;
        var lineStart = box.Text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
        // 行首已有 1~5 级标题前缀时逐级加深（到 5 回到 1，galaxy applyFormat 的 heading 语义）。
        var current = 0;
        while (current < 5 && lineStart + current < box.Text.Length && box.Text[lineStart + current] == '#') current++;
        if (current > 0 && lineStart + current < box.Text.Length && box.Text[lineStart + current] == ' ')
        {
            var next = current < 5 ? new string('#', current + 1) : "#";
            var rest = box.Text[(lineStart + current + 1)..];
            ApplyEditedText(box.Text[..lineStart] + next + " " + rest, lineStart + next.Length + 1, lineStart + next.Length + 1);
            return;
        }
        ApplyEditedText(box.Text[..lineStart] + "## " + box.Text[lineStart..], lineStart + 3, lineStart + 3);
    }

    void WrapSelection(string before, string after, string action, string sample)
    {
        var box = BodyBox;
        var start = box.SelectionStart;
        var selected = box.Text.Substring(start, box.SelectionLength);
        var text = selected.Length > 0 ? selected : sample;
        ApplyEditedText(box.Text[..start] + before + text + after + box.Text[(start + box.SelectionLength)..],
            start + before.Length, start + before.Length + text.Length);
    }

    (int LineStart, int LineEnd) SelectedBlock(TextBox box)
    {
        var start = box.SelectionStart;
        var end = box.SelectionStart + box.SelectionLength;
        var lineStart = box.Text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        int lineEnd = box.Text.Length;
        if (end < box.Text.Length)
        {
            var next = box.Text.IndexOf('\n', end);
            if (next >= 0) lineEnd = next;
        }
        return (lineStart, lineEnd);
    }

    void PrefixLines(string prefix, string action)
    {
        var box = BodyBox;
        var (lineStart, lineEnd) = SelectedBlock(box);
        var lines = box.Text[lineStart..lineEnd].Replace("\r", "").Split('\n');
        var body = string.Join("\n", lines.Select(line => line.StartsWith(prefix, StringComparison.Ordinal) ? line : prefix + line));
        var caret = lineStart + Math.Min(prefix.Length + box.SelectionStart - lineStart, body.Length);
        ApplyEditedText(box.Text[..lineStart] + body + box.Text[lineEnd..], caret, caret);
    }

    void PrefixNumberedLines(string action)
    {
        var box = BodyBox;
        var (lineStart, lineEnd) = SelectedBlock(box);
        var lines = box.Text[lineStart..lineEnd].Replace("\r", "").Split('\n');
        var body = string.Join("\n", lines.Select((line, index) => $"{index + 1}. {line}"));
        var caret = lineStart + Math.Min(3 + box.SelectionStart - lineStart, body.Length);
        ApplyEditedText(box.Text[..lineStart] + body + box.Text[lineEnd..], caret, caret);
    }

    void WrapLines(string blockBefore, string blockAfter, string inlineBefore, string inlineAfter, string action, string sample)
    {
        var box = BodyBox;
        var start = box.SelectionStart;
        var selected = box.Text.Substring(start, box.SelectionLength);
        if (selected.Contains('\n'))
        {
            ApplyEditedText(box.Text[..start] + blockBefore + selected + blockAfter + box.Text[(start + box.SelectionLength)..],
                start + blockBefore.Length, start + blockBefore.Length + selected.Length);
            return;
        }
        WrapSelection(inlineBefore, inlineAfter, action, sample);
    }

    /// <summary>程序性改写正文：一次 TextChanged，恢复光标后保持编辑链路（草稿/撤销栈）走原有路径。</summary>
    void ApplyEditedText(string text, int selectionStart, int selectionEnd)
    {
        var box = BodyBox;
        box.Text = text;
        if (selectionStart >= 0 && selectionStart <= box.Text.Length)
            box.Select(selectionStart, Math.Clamp(selectionEnd - selectionStart, 0, box.Text.Length - selectionStart));
        box.Focus(FocusState.Programmatic);
    }

    // ---------------- 撤销 / 重做（节流快照栈） ----------------

    void PushUndoSnapshot(bool force)
    {
        if (applyingHistory || store.Draft is not { } draft) return;
        var now = DateTimeOffset.UtcNow;
        if (!force && now - lastSnapshot < SnapshotInterval) return;
        lastSnapshot = now;
        undoStack.Add((draft.Title, draft.Body));
        if (undoStack.Count > UndoLimit) undoStack.RemoveAt(0);
        redoStack.Clear();
    }

    void UndoClicked(object sender, RoutedEventArgs args) => RestoreHistory(undoStack, redoStack, Strings.Notes_Undo);
    void RedoClicked(object sender, RoutedEventArgs args) => RestoreHistory(redoStack, undoStack, Strings.Notes_Redo);

    void RestoreHistory(List<(string Title, string Body)> pop, List<(string Title, string Body)> push, string action)
    {
        if (pop.Count == 0 || store.Draft is not { } draft) return;
        var next = pop[^1];
        pop.RemoveAt(pop.Count - 1);
        push.Add((draft.Title, draft.Body));
        applyingHistory = true;
        try
        {
            TitleBox.Text = next.Title;
            BodyBox.Text = next.Body;
            store.Edit(next.Title, next.Body);
        }
        finally { applyingHistory = false; }
        UpdateButtons();
        Status.Text = store.HasChanges ? Strings.Common_NotSaved : Strings.Notes_Saved;
        RefreshToolbar();
        if (splitView) BuildSplitPreview();
    }
}
