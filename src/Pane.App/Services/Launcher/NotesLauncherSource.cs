using Pane.Contracts;
using Pane.Localization;

namespace Pane.Services.Launcher;

/// <summary>便签启动器入口（notes-mvp §3）：便签管理、快速新建和「桌面便签」键盘弹层。
/// 关键词对照 galaxy 命令清单：便签管理（便签 / notes / bq / bianqian）、新建桌面便签、桌面便签。</summary>
internal sealed class NotesLauncherSource(Func<bool> available, Action openManager, Action newNote, Action openPanel) : EntrySource(LauncherGroups.Commands, 4, "note:")
{
    readonly PinyinTable table = new();

    protected override SearchEntry[] Entries(SourceContext c) => available() ? Entries() : [];

    SearchEntry[] Entries() =>
    [
        new("note:open", LauncherItemKind.Command, table.CreateLocalized(Strings.Notes_OpenManager, Strings.Simplified(nameof(Strings.Notes_OpenManager))),
            "notes bq bianqian 便签管理 open", "pencil"),
        new("note:new", LauncherItemKind.Command, table.CreateLocalized(Strings.Notes_NewDesktop, Strings.Simplified(nameof(Strings.Notes_NewDesktop))),
            "new note new desktop note 新建便签 新增便箋 xjbianqian", "plus"),
        new("note:panel", LauncherItemKind.Command, table.CreateLocalized(Strings.Notes_DesktopNotesPanel, Strings.Simplified(nameof(Strings.Notes_DesktopNotesPanel))),
            "desktop notes 桌面便签", "eye"),
    ];

    internal override SearchEntry[] ListEntries(SourceContext c) => Entries(c).Select(e => e with { Subtitle = Strings.Launcher_CommandSubtitle }).ToArray();

    public override Task ExecuteAsync(LauncherItem item, SourceContext c, CancellationToken token)
    {
        switch (item.Id)
        {
            case "note:open": openManager(); break;
            case "note:new": newNote(); break;
            default: openPanel(); break;
        }
        return Task.CompletedTask;
    }
}
