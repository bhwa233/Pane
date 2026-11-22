using Pane.Services.Notes;
using Pane.Settings;

// notes-mvp：纯文本 NoteStore 与共享编辑会话（NoteSessions）的自动验证。
// 覆盖写盘与历史分离、版本保留、恢复语义、TXT 读写、建议文件名、整条提醒调度，
// 以及共享草稿的保存顺序、晚到成功不覆盖新草稿和空便签不留痕。
static class NoteHistoryTests
{
    public static int Run()
    {
        var directory = Directory.CreateTempSubdirectory("pane-note-history-");
        try { return Run(directory.FullName); }
        finally { directory.Delete(recursive: true); }
    }

    static int Run(string directory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }

        // ---------------- NoteStore：写盘与历史分离（notes-mvp §4） ----------------

        var store = new NoteStore(directory);
        store.Load();
        var created = store.Create("Old title", "Old text", "Work");
        Check(created.Title == "Old title" && created.Category == "Work" && store.Versions(created.Id).Count == 0,
            "a created note has no previous version");

        var saved = store.Save(created with { Title = "New title", Body = "New text" }, history: false);
        Check(saved.Title == "New title" && store.Versions(created.Id).Count == 0 && saved.Updated > created.Updated,
            "autosave commits only the current body without recording a version");

        var unchanged = store.Save(saved with { Body = "New text" }, history: true);
        Check(unchanged.Updated == saved.Updated && store.Versions(created.Id).Count == 0,
            "saving identical content records no version and does not advance Updated");

        var withHistory = store.Save(saved with { Body = "Edited text" }, history: true);
        var version = store.Versions(created.Id).Single();
        Check(store.ReadVersion(created.Id, version).Body == "New text",
            "an explicit save records the replaced content as a version");

        // 连续自动保存不挤满历史；显式时机才记版。
        for (int index = 0; index < 32; index++)
            saved = store.Save(saved with { Body = $"Autosave body {index}" }, history: false);
        Check(store.Versions(created.Id).Count == 1, "a run of autosaves adds no versions");

        for (int index = 0; index < 32; index++)
            saved = store.Save(saved with { Body = $"History body {index}" }, history: true);
        var retained = store.Versions(created.Id);
        Check(retained.Count == 30 && retained.All(date => date > version),
            "old versions are pruned on disk and the newest 30 remain");

        // 恢复：被替换内容记为历史，当前分类不被旧正文带回。
        // 30 版上限已把最早的「New text」裁掉，用现存最老的版本验证恢复。
        var restored = store.Restore(created.Id, store.ReadVersion(created.Id, retained.Last()));
        Check(restored.Body == "History body 1" && restored.Category == "Work" && restored.Title == "New title",
            "restore keeps the current category and restores the previewed content");
        var replaced = store.Versions(created.Id).First();
        Check(store.ReadVersion(created.Id, replaced).Body == saved.Body, "restore remains reversible");

        // 回收 / 恢复 / 永久删除。
        // 队列后台写入时，UI 已取得的列表必须能独立于活字典枚举。
        var documentsBeforeWrite = store.Documents;
        var other = store.Create("Other note", "Its own body", "Personal");
        Check(!documentsBeforeWrite.Any(note => note.Id == other.Id) && store.Documents.Any(note => note.Id == other.Id),
            "a captured note listing stays stable across a later write");
        Check(store.Recycle(other.Id) && store.Find(other.Id) == null && store.RecycledDocuments.Single().Id == other.Id,
            "recycling moves the note out of the active list but retains its content");
        Check(store.RestoreRecycled(other.Id) && store.Find(other.Id)?.Title == "Other note" && !store.RecycledDocuments.Any(),
            "restoring a recycled note retains its ID");
        store.Recycle(other.Id);
        Check(store.DeleteRecycled(other.Id) && !File.Exists(store.PathFor(other.Id)) &&
            !Directory.EnumerateFiles(Path.Combine(directory, ".history"), $"{other.Id:N}-*").Any() &&
            store.Find(created.Id) != null,
            "permanently deleting a recycled note removes only its own content and history");

        // ---------------- TXT 读取与建议文件名（notes-mvp §3） ----------------

        var files = Directory.CreateDirectory(Path.Combine(directory, "files")).FullName;
        var text = Path.Combine(files, "会议.txt");
        File.WriteAllText(text, "# 议程\r\n\r\n- 测试\r\n", new System.Text.UTF8Encoding(true));
        var before = File.ReadAllBytes(text);
        var imported = NoteStore.ReadFile(text);
        Check(imported.Title == "会议" && imported.Body == "# 议程\n\n- 测试\n" && File.ReadAllBytes(text).SequenceEqual(before),
            "import accepts plain text, strips the BOM, and does not change the source");

        var markdown = Path.Combine(files, "会议.md");
        File.WriteAllText(markdown, "# 议程");
        bool refused = false;
        try { NoteStore.ReadFile(markdown); }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "the reader rejects non-.txt files");

        File.WriteAllBytes(Path.Combine(files, "broken.txt"), [0xC3, 0x28]);
        refused = false;
        try { NoteStore.ReadFile(Path.Combine(files, "broken.txt")); }
        catch (System.Text.DecoderFallbackException) { refused = true; }
        Check(refused, "malformed UTF-8 is rejected instead of importing replacement characters");

        Check(NoteStore.SuggestedFileName("标题<b>:? ", "正文", "未命名便签") == "标题b",
            "suggested names drop illegal characters and trailing separators");
        Check(NoteStore.SuggestedFileName("", "第一行\n第二行", "未命名便签") == "第一行",
            "an empty title falls back to the first non-empty body line");
        Check(NoteStore.SuggestedFileName("  ", "", "未命名便签") == "未命名便签", "fully empty notes use the fallback");
        Check(NoteStore.SuggestedFileName("CON", "", "未命名便签").StartsWith('_'), "reserved device names are replaced");
        Check(NoteStore.SuggestedFileName(new string('长', 100), "", "未命名便签").Length <= 80, "long suggestions are truncated");

        // 整条提醒：旧任务条目不再调度（notes-mvp §5）。
        var defaults = new PaneSettings(1, new(false, true, ThemeMode.Dark, "#FF5874C9", 20, 1,
            new(.2, .2, 1, 1, 1, 0), new(.2, .2, 1, 1, 1, 0)),
            new(new(KeyModifiers.Alt, 32)), new(new(KeyModifiers.Ctrl, 86)));
        var settingsDirectory = Path.Combine(directory, "reminder-settings");
        var settingsStore = new SettingsStore(settingsDirectory, defaults);
        var due = DateTimeOffset.UnixEpoch.AddDays(1);
        settingsStore.Update(settings => NoteReminderSchedule.Set(settings, created.Id, due.AddHours(1)));
        settingsStore.Update(settings => settings with
        {
            NoteTaskReminders = new() { [created.Id] = new() { ["Send report"] = due } }
        });
        settingsStore = new SettingsStore(settingsDirectory, defaults);
        settingsStore.Load();
        Check(NoteReminderSchedule.Next(settingsStore.Current) == new NoteReminder(created.Id, due.AddHours(1)),
            "legacy task entries are no longer scheduled");
        settingsStore.Update(settings => NoteReminderSchedule.RemoveNote(settings, created.Id));
        Check(settingsStore.Current.NoteTaskReminders?.ContainsKey(created.Id) != true,
            "removing a note also clears its legacy task entries");

        // ---------------- 旧格式目录实测（notes-mvp §1 读取规则 + §8 第 5 步回归） ----------------
        // 构造一份升级前的真实数据目录：正文带 Images / EditorMode 字段、.history 版本同样带旧字段、
        // .images 目录原样存在；升级后的 Pane 必须忽略旧字段、正文按纯文本加载、旧字段不参与任何判断。
        var legacyRoot = Path.Combine(directory, "legacy-notes");
        Directory.CreateDirectory(Path.Combine(legacyRoot, ".history"));
        Directory.CreateDirectory(Path.Combine(legacyRoot, ".images"));
        var legacyId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(legacyRoot, $"{legacyId:N}.json"),
            $$"""{"Id":"{{legacyId}}","Title":"旧格式","Body":"# 旧正文\n","Updated":"2026-11-01T08:00:00Z","Category":"归档","Images":["d41d8cd98f00b204e9800998ecf8427e"],"EditorMode":1,"Recycled":false}""",
            System.Text.Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(legacyRoot, ".images", $"{legacyId:N}-d41d8cd98f00b204e9800998ecf8427e.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///8/AAn7A/0FQ0XKAAAAAElFTkSuQmCC"));
        var legacyVersionStamp = new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(legacyRoot, ".history",
                $"{legacyId:N}-{legacyVersionStamp.UtcTicks.ToString("D19", System.Globalization.CultureInfo.InvariantCulture)}.json"),
            $$"""{"Id":"{{legacyId}}","Title":"旧格式旧版","Body":"旧正文旧版","Updated":"2026-10-31T08:00:00+00:00","Category":"","Images":null,"EditorMode":0}""",
            System.Text.Encoding.UTF8);
        var legacy = new NoteStore(legacyRoot);
        legacy.Load();
        var legacyNote = legacy.Find(legacyId);
        Check(legacyNote is { Title: "旧格式", Body: "# 旧正文\n", Category: "归档" } && legacy.UnreadableCount == 0,
            "an upgraded store ignores legacy Images/EditorMode fields and loads the body as plain text");
        Check(legacy.Versions(legacyId).Count == 1 && legacy.ReadVersion(legacyId, legacy.Versions(legacyId).Single()).Body == "旧正文旧版",
            "legacy history versions remain readable");
        // 旧字段不参与保存判断：下次正文提交写出 MVP 模型后不再保留。
        legacy.Save(legacyNote! with { Body = "# 旧正文\n已改" }, history: false);
        Check(!File.ReadAllText(Path.Combine(legacyRoot, $"{legacyId:N}.json")).Contains("EditorMode") &&
            Directory.GetFiles(Path.Combine(legacyRoot, ".images")).Length == 1,
            "the next body commit writes the MVP model and leaves the .images directory untouched");

        // ---------------- NoteSessions：共享草稿的保存顺序（notes-mvp §2） ----------------

        var sessionDirectory = Path.Combine(directory, "sessions");
        var sessionStore = new NoteStore(sessionDirectory);
        sessionStore.Load();
        var pending = new Queue<Action>();
        ManualTimer? lastTimer = null;
        var hub = new NoteSessions(sessionStore, action => { lock (pending) pending.Enqueue(action); }, () => lastTimer = new ManualTimer());
        // Fire 在调用线程内同步入队写盘任务；写盘线程完成后才把回调放入 pending。
        // 等到回调出现即代表写盘已结束，随后再执行回调，整个流程是确定性的。
        void RunPendingWrites()
        {
            lastTimer?.Fire();
            WaitEnqueued();
            DrainPending();
        }
        void WaitEnqueued() { while (true) { lock (pending) { if (pending.Count > 0) return; } Thread.Sleep(5); } }
        void DrainPending() { lock (pending) { while (pending.Count > 0) pending.Dequeue()(); } }

        // 空的新便签不留痕。
        var blank = hub.New();
        hub.Edit(blank, "", "  ", null, null);
        Check(hub.Flush(blank) && hub.Find(blank.Id) == null && !Directory.EnumerateFiles(sessionDirectory, "*.json").Any(),
            "an empty new note is discarded without a record");

        // 两处编辑同一份草稿：实时同步、写盘走快照。
        var session = hub.New();
        hub.Attach(session);
        NoteSessions.Session? received = null;
        object? receivedSource = null;
        hub.Changed += (changed, source) => { received = changed; receivedSource = source; };
        hub.Edit(session, "标题", "第一行", null, "manager");
        Check(received == session && (string?)receivedSource == "manager",
            "edits update the shared draft and broadcast with their source");
        hub.Edit(session, "标题", "标题\n第二行", "Work", "card");
        Check((string?)receivedSource == "card" && session.Body == "标题\n第二行" && session.Category == "Work",
            "another view's edit updates the same shared draft");
        RunPendingWrites();
        Check(session.Persisted && session.SavedRevision == session.Revision && sessionStore.Find(session.Id) is { Body: "标题\n第二行" },
            "an autosave persists the snapshot for the latest revision");

        // 晚到的成功结果只推进已保存修订，不覆盖更新的草稿。
        hub.Edit(session, "标题", "更新的草稿", null, null);
        lastTimer?.Fire();
        WaitEnqueued();  // 写盘完成、回调已入队但先不执行。
        Check(hub.Flush(session) && session.Body == "更新的草稿" && sessionStore.Find(session.Id) is { Body: "更新的草稿" },
            "a synchronous flush commits the newest draft");
        DrainPending();  // 旧快照的迟到结果因 Epoch 作废。
        Check(session.SavedRevision == session.Revision && session.Failed == false,
            "a late write result does not overwrite the newer draft");

        // 首次修改记一版历史；停顿窗口内的后续自动保存不再记（notes-mvp §4）。
        var seed = sessionStore.Create("会话种子", "初始正文");
        var live = hub.Open(seed.Id);
        hub.Attach(live);
        hub.Edit(live, "会话种子", "首次修改的正文", null, "manager");
        RunPendingWrites();
        Check(sessionStore.Versions(live.Id).Count == 1 && sessionStore.ReadVersion(live.Id, sessionStore.Versions(live.Id).Single()).Body == "初始正文",
            "the first body edit records the pre-edit content");
        for (int index = 0; index < 5; index++)
        {
            hub.Edit(live, "会话种子", $"继续输入 {index}", null, "manager");
            RunPendingWrites();
        }
        Check(sessionStore.Versions(live.Id).Count == 1,
            "continued autosaves within the idle window do not flood the history");

        // 引用归零且已结清才释放；未结清的会话保留草稿。
        hub.Detach(live);
        Check(hub.Find(live.Id) == null, "a settled session is released when its last view closes");
        hub.Detach(session);
        Check(hub.Find(session.Id) == null, "a settled new-note session is released when its last view closes");
        hub.Dispose();
        return checks;
    }

    sealed class ManualTimer : NoteTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public event EventHandler? Tick;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }
}

