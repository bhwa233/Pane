using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Pane.Localization;
using Pane.Settings;

namespace Pane.Views;

public sealed partial class SettingsView
{
    const string QuickLinkFormKey = "quick-link";
    Guid? editingQuickLink;
    QuickLink? quickLinkPreset;
    bool showQuickLinkPresets;

    void RenderQuickLinks(LauncherSettings settings)
    {
        var rows = new List<FrameworkElement>();
        var reserved = QuickLinkRules.ReservedKeywords(settings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var link in settings.QuickLinks)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            label.Children.Add(Text(link.Name, LabelProto));
            label.Children.Add(Chip(link.Keyword));
            var conflict = reserved.Contains(link.Keyword);
            rows.Add(ListRow(link.Kind == QuickLinkKind.Web ? "globe" : "terminal", label,
                conflict ? Strings.QuickLinks_ReservedKeyword : link.Target, !conflict,
                Switch(link.Enabled, enabled => UpdateQuickLink(link, link with { Id = Guid.NewGuid(), Enabled = enabled })),
                IconButton("pencil", Strings.Common_Edit, () => OpenQuickLinkForm(link.Id)),
                IconButton("trash", Strings.Common_Delete, () => UpdateQuickLink(link, null))));
            if (editingQuickLink == link.Id) rows.Add(Reveal(QuickLinkFormKey, [QuickLinkForm(link)]));
        }
        if (editingQuickLink == Guid.Empty) rows.Add(Reveal(QuickLinkFormKey, [QuickLinkForm(null, quickLinkPreset)]));
        else if (editingQuickLink == null)
        {
            var add = Button(Strings.QuickLinks_Add, "plus", () => OpenQuickLinkForm(Guid.Empty));
            Enable(add, settings.QuickLinks.Length < QuickLinkRules.EntryLimit);
            var presets = Button(Strings.QuickLinks_Presets, showQuickLinkPresets ? "chev-u" : "chev-d", () => { showQuickLinkPresets = !showQuickLinkPresets; Render(); });
            Enable(presets, settings.QuickLinks.Length < QuickLinkRules.EntryLimit);
            rows.Add(AddRow(add, presets));
            if (showQuickLinkPresets)
                foreach (var (title, keyword, target) in Presets())
                {
                    var use = Button(Strings.QuickLinks_UsePreset, "plus", () =>
                    {
                        var key = reserved.Contains(keyword) || settings.QuickLinks.Any(link => link.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase)) ? "" : keyword;
                        OpenQuickLinkForm(Guid.Empty, new(Guid.Empty, title, key, QuickLinkKind.Web, target));
                    });
                    Enable(use, !settings.QuickLinks.Any(link => link.Kind == QuickLinkKind.Web && link.Target == target) &&
                        !settings.SearchEngines.Any(engine => engine.UrlTemplate == target));
                    rows.Add(ListRow("globe", title, keyword, true, use));
                }
        }
        Group(Strings.QuickLinks_Title, rows.ToArray());
    }

    static (string Title, string Keyword, string Target)[] Presets() =>
    [
        (Strings.QuickLinks_PresetZhihu, "zh", "https://www.zhihu.com/search?type=content&q={query}"),
        (Strings.QuickLinks_PresetMaps, "map", "https://www.amap.com/search?query={query}"),
        (Strings.QuickLinks_PresetRepo, "repo", "https://github.com/{owner}/{repo}"),
        (Strings.QuickLinks_PresetObsidian, "obs", "obsidian://open?vault={vault}&file={file}"),
    ];

    void OpenQuickLinkForm(Guid id, QuickLink? preset = null)
    {
        editingQuickLink = id;
        quickLinkPreset = preset;
        showQuickLinkPresets = false;
        revealKey = QuickLinkFormKey;
        Render();
    }

    void UpdateQuickLink(QuickLink previous, QuickLink? next)
    {
        try { SaveQuickLink(previous, next); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { app.ReportFailure(Strings.QuickLinks_Title, error); }
        if (editingQuickLink == previous.Id) editingQuickLink = null;
        Render();
    }

    void SaveQuickLink(QuickLink? previous, QuickLink? next) => app.Store.Update(settings =>
    {
        var launcher = settings.Launcher;
        var links = launcher.QuickLinks.ToList();
        if (previous == null)
        {
            if (next != null) links.Add(next);
        }
        else
        {
            var index = links.IndexOf(previous);
            if (index < 0) throw new InvalidOperationException(Strings.QuickLinks_Changed);
            if (next == null) links.RemoveAt(index);
            else links[index] = next;
        }
        if (next != null && !string.Equals(previous?.Keyword, next.Keyword, StringComparison.OrdinalIgnoreCase) &&
            QuickLinkRules.ReservedKeywords(launcher).Contains(next.Keyword, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(Strings.FormatName(Strings.Settings_Launcher_EngineKeywordDuplicate, next.Keyword));
        return settings with { Launcher = launcher with { QuickLinks = links.ToArray() } };
    });

    FrameworkElement QuickLinkForm(QuickLink? editing, QuickLink? preset = null)
    {
        var initial = editing ?? preset;
        var kind = initial?.Kind ?? QuickLinkKind.Web;
        var name = LinkField(Strings.Common_Name, initial?.Name, 80);
        var keyword = LinkField(Strings.Settings_Launcher_EngineKeyword, initial?.Keyword, 32);
        var target = LinkField(Strings.QuickLinks_Address, initial?.Target, 4096);
        var arguments = LinkField(Strings.QuickLinks_Arguments, editing?.Arguments, 4096);
        arguments.AcceptsReturn = true;
        arguments.Height = 80;
        var directory = LinkField(Strings.QuickLinks_WorkingDirectory, editing?.WorkingDirectory, 4096);
        var error = Text("", DescProto);
        error.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var programOptions = new StackPanel { Spacing = 6 };
        programOptions.Children.Add(Wrap(arguments));
        programOptions.Children.Add(Wrap(directory));
        var pick = IconButton("folder", Strings.QuickLinks_Program, () => _ = PickAsync());
        var targetRow = new Grid { ColumnSpacing = 6 };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetRow.Children.Add(Wrap(target));
        Grid.SetColumn(pick, 1);
        targetRow.Children.Add(pick);
        void ApplyKind()
        {
            var program = kind == QuickLinkKind.Program;
            programOptions.Visibility = pick.Visibility = program ? Visibility.Visible : Visibility.Collapsed;
            target.PlaceholderText = program ? Strings.QuickLinks_Program : Strings.QuickLinks_Address;
            AutomationProperties.SetName(target, target.PlaceholderText);
        }
        ApplyKind();
        var choices = Seg([("Web", Strings.Launcher_Group_Web), ("Program", Strings.QuickLinks_Program)], kind.ToString(), value =>
        {
            var next = Enum.Parse<QuickLinkKind>(value);
            if (next == kind) return;
            kind = next;
            target.Text = "";
            error.Text = "";
            ApplyKind();
        });
        var save = Button(Strings.Common_Save, "check", () =>
        {
            var link = new QuickLink(Guid.NewGuid(), name.Text.Trim(), keyword.Text.Trim(), kind, target.Text.Trim(),
                kind == QuickLinkKind.Program ? arguments.Text : "", kind == QuickLinkKind.Program ? directory.Text.Trim() : "", editing?.Enabled ?? true);
            try { SaveQuickLink(editing, link); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { error.Text = App.FailureReason(exception) ?? Strings.QuickLinks_Invalid; return; }
            editingQuickLink = null;
            quickLinkPreset = null;
            Render();
        });
        var cancel = Button(Strings.Common_Cancel, null, () => Collapse(QuickLinkFormKey, () => { editingQuickLink = null; quickLinkPreset = null; Render(); }));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var form = new StackPanel { Padding = new Thickness(14, 10, 14, 10), Spacing = 6 };
        form.Children.Add(choices);
        form.Children.Add(Wrap(name));
        form.Children.Add(Wrap(keyword));
        form.Children.Add(targetRow);
        form.Children.Add(programOptions);
        form.Children.Add(error);
        form.Children.Add(buttons);
        form.Loaded += (_, _) => { name.Focus(FocusState.Programmatic); name.StartBringIntoView(); };
        return form;

        async Task PickAsync()
        {
            try { if (await PickPortableProgram(executableOnly: true) is { } path && kind == QuickLinkKind.Program) target.Text = path; }
            catch (Exception exception) { error.Text = App.FailureReason(exception) ?? Strings.QuickLinks_Invalid; }
        }
    }

    TextBox LinkField(string label, string? value, int limit)
    {
        var box = Field(label, double.NaN);
        box.Text = value ?? "";
        box.MaxLength = limit;
        AutomationProperties.SetName(box, label);
        return box;
    }
}
