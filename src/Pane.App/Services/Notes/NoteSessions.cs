using System.Collections.Concurrent;

namespace Pane.Services.Notes;

/// <summary>共享编辑会话的调度时钟（便于脱离 DispatcherQueue 做自动验证）。</summary>
internal interface NoteTimer : IDisposable
{
    TimeSpan Interval { get; set; }
    bool IsRepeating { get; set; }
    event EventHandler Tick;
    void Start();
    void Stop();
}

/// <summary>按便签 ID 管理的共享编辑会话（notes-mvp §2）：管理器和桌面窗口绑定同一份草稿，
/// 任一处输入后另一处立即更新。自动保存按 ID 合并短时间内的连续输入；同一 ID 的写入串行；
/// 写盘使用当时的快照，晚到的成功结果只推进已保存修订，不覆盖更新的草稿。
/// 显式保存、折叠、隐藏、关闭、切换以及历史恢复复用同一 Flush 入口。</summary>
internal sealed class NoteSessions : IDisposable
{
    internal sealed class Session
    {
        public required Guid Id { get; init; }
        public string Title = "", Body = "", Category = "";
        /// <summary>每次接受的输入 +1。</summary>
        public long Revision;
        /// <summary>最近成功写盘对应的修订号。</summary>
        public long SavedRevision;
        /// <summary>磁盘上已有这条便签（未落盘的空新便签为 false）。</summary>
        public bool Persisted;
        /// <summary>最近一次写盘失败。</summary>
        public bool Failed;
        /// <summary>本次编辑会话尚未记过「首次正文修改」的历史；只在首次正文修改前置位，
        /// 记录一次后不再置位，避免连续自动保存挤满 30 版上限（notes-mvp §4）。</summary>
        public bool BodyTouched;
        /// <summary>会话已建立历史基线（新建落盘或首次记版后）。</summary>
        public bool HistoryRecorded;
        /// <summary>上一次记历史的时刻（停止输入数分钟后再记一版）。</summary>
        public DateTimeOffset? LastHistoryAt;
        /// <summary>最近一次成功写盘的正文；标题或分类单独变化不记正文历史。</summary>
        public string SavedBody = "";
        /// <summary>同步 Flush 递增，使排队中的旧快照作废。</summary>
        public long Epoch;
        public NoteTimer? Autosave;
        /// <summary>绑定该会话的视图数（管理器 / 桌面卡）；归零且已结清才真正释放。</summary>
        public int Views;
        public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Body);
    }

    sealed record WriteJob(Session Session, string Title, string Body, string Category, long Revision, long Epoch, bool WithHistory);

    readonly NoteStore store;
    readonly Action<Action> marshal;
    readonly Func<NoteTimer> timerFactory;
    readonly Dictionary<Guid, Session> sessions = [];
    readonly Dictionary<NoteTimer, Session> timers = [];
    readonly BlockingCollection<WriteJob> writes = new(new ConcurrentQueue<WriteJob>());
    readonly object gate = new();
    Thread? writer;
    bool disposed;

    /// <summary>内容更新（source 视图跳过回发，避免循环和光标跳动）。</summary>
    public event Action<Session, object?>? Changed;
    public event Action<Session>? Saved;
    public event Action<Session>? SaveFailed;

    static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(600);
    /// <summary>停止输入超过该时长后的下一次写盘再记一版历史（notes-mvp §4「数分钟」）。</summary>
    static readonly TimeSpan HistoryIdle = TimeSpan.FromMinutes(5);

    public NoteSessions(NoteStore store, Action<Action> marshal, Func<NoteTimer> timerFactory)
    {
        this.store = store;
        this.marshal = marshal;
        this.timerFactory = timerFactory;
        store.Changed += StoreChanged;
    }

    /// <summary>外部提交（导入、恢复、回收）后，已结清且目标已不存在的会话直接释放。</summary>
    void StoreChanged(object? sender, EventArgs args)
    {
        lock (gate)
        {
            foreach (var session in sessions.Values.ToArray())
                if (session.Persisted && session.Revision == session.SavedRevision && !session.Failed &&
                    store.Find(session.Id) is not { } saved)
                    Forget(session);
        }
    }

    /// <summary>取得或打开指定便签的会话；便签必须已存在于存储。</summary>
    public Session Open(Guid id)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(id, out var existing)) return existing;
            var note = store.Find(id) ?? throw new KeyNotFoundException();
            var session = new Session { Id = id, Title = note.Title, Body = note.Body, SavedBody = note.Body,
                Category = note.Category, Persisted = true };
            sessions.Add(id, session);
            return session;
        }
    }

    /// <summary>新建空白会话：不写盘，直到有实际内容；关闭时若仍为空直接丢弃（notes-mvp §2）。</summary>
    public Session New()
    {
        lock (gate)
        {
            var session = new Session { Id = Guid.NewGuid() };
            sessions.Add(session.Id, session);
            return session;
        }
    }

    public Session? Find(Guid id)
    {
        lock (gate) return sessions.GetValueOrDefault(id);
    }

    /// <summary>当前全部活跃会话（快照）。</summary>
    public IReadOnlyList<Session> List()
    {
        lock (gate) return sessions.Values.ToArray();
    }

    /// <summary>视图绑定会话：引用计数 +1；会话不在映射中时重新挂回（视图持有的是同一对象）。</summary>
    public void Attach(Session session)
    {
        lock (gate)
        {
            if (sessions.GetValueOrDefault(session.Id) != session) sessions[session.Id] = session;
            session.Views++;
        }
    }

    /// <summary>视图释放会话：引用归零且已结清才真正丢弃（notes-mvp §2「最后一个视图关闭后，
    /// 只有已经保存的会话才可释放」）。</summary>
    public void Detach(Session session)
    {
        lock (gate)
        {
            if (session.Views > 0) session.Views--;
            if (session.Views > 0) return;
            if (session.Persisted && session.Revision == session.SavedRevision && !session.Failed ||
                !session.Persisted && session.IsEmpty) Forget(session);
        }
    }

    /// <summary>历史恢复等外部改写后，把最新内容广播给所有绑定的视图（notes-mvp §4：恢复后各窗口显示同一内容）。</summary>
    public void BroadcastRestored(Session session)
    {
        lock (gate)
        {
            if (sessions.GetValueOrDefault(session.Id) != session) return;
        }
        Changed?.Invoke(session, null);
    }

    /// <summary>用户输入：在 UI 线程更新共享草稿并广播；source 视图不回发。
    /// 同一窗口的程序赋值由视图自身的 updating 保护，不经过这里。</summary>
    public void Edit(Session session, string title, string body, string? category, object? source)
    {
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        lock (gate)
        {
            if (sessions.GetValueOrDefault(session.Id) != session) return;
            var touched = normalized != session.Body;
            session.Title = title ?? "";
            session.Body = normalized;
            session.Category = category?.Trim() ?? session.Category;
            session.Revision++;
            // 首次正文修改置位一次；基线建立后由停顿窗口决定是否再记（notes-mvp §4）。
            if (touched && !session.HistoryRecorded) session.BodyTouched = true;
            session.Failed = false;
        }
        Changed?.Invoke(session, source);
        ScheduleAutosave(session);
    }

    void ScheduleAutosave(Session session)
    {
        NoteTimer timer;
        lock (gate)
        {
            if (disposed) return;
            if (session.Autosave is { } existing) timer = existing;
            else
            {
                timer = timerFactory();
                timer.Interval = AutosaveDelay;
                timer.IsRepeating = false;
                timer.Tick += AutosaveTick;
                session.Autosave = timer;
                timers[timer] = session;
            }
        }
        timer.Stop();
        timer.Start();
    }

    void AutosaveTick(object? sender, EventArgs args)
    {
        if (sender is not NoteTimer timer) return;
        lock (gate)
        {
            timer.Stop();
            if (timers.GetValueOrDefault(timer) is not { } session) return;
            if (session.IsEmpty && !session.Persisted) return;
            EnqueueWrite(session);
        }
    }

    /// <summary>同步保存入口：显式保存、折叠、隐藏、关闭、切换便签和恢复前都走这里。
    /// 排队中的旧快照作废（Epoch 递增）；失败保留当前输入与窗口并返回 false。
    /// 空的新便签直接丢弃，不留记录、布局或胶囊（notes-mvp §2）。</summary>
    public bool Flush(Session session)
    {
        lock (gate)
        {
            if (disposed || sessions.GetValueOrDefault(session.Id) != session) return true;
            StopAutosave(session);
            if (!session.Persisted)
            {
                if (session.IsEmpty) { Forget(session); return true; }
            }
            else if (session.Revision == session.SavedRevision && !session.Failed) return true;
            session.Epoch++;
            return WriteNow(session);
        }
    }

    /// <summary>程序退出 / 模块停用前保存所有未写盘的会话；任何失败返回 false。</summary>
    public bool FlushAll()
    {
        var all = true;
        lock (gate)
        {
            foreach (var session in sessions.Values.ToArray())
                if (!Flush(session)) all = false;
        }
        return all;
    }

    /// <summary>释放会话：只有已经保存的会话才可释放，未保存的保留草稿等待 Flush 收口。</summary>
    public void Forget(Session session)
    {
        lock (gate)
        {
            StopAutosave(session);
            sessions.Remove(session.Id);
        }
    }

    /// <summary>便签被删除后强制丢弃会话（草稿已由调用方先结清）。</summary>
    public void Forget(Guid id)
    {
        lock (gate)
        {
            if (sessions.GetValueOrDefault(id) is not { } session) return;
            StopAutosave(session);
            sessions.Remove(id);
        }
    }

    void StopAutosave(Session session)
    {
        if (session.Autosave is not { } timer) return;
        timer.Stop();
        timer.Tick -= AutosaveTick;
        timer.Dispose();
        timers.Remove(timer);
        session.Autosave = null;
    }

    bool WriteNow(Session session)
    {
        try
        {
            // 折叠／隐藏／关闭／切换（notes-mvp §4）：正文有变化才记一版，标题或分类单独变化不记。
            var withHistory = session.Persisted && session.Body != session.SavedBody;
            var wasPersisted = session.Persisted;
            var saved = !wasPersisted
                ? store.Create(session.Title, session.Body, session.Category, session.Id)
                : store.Save(new(session.Id, session.Title, session.Body, default) { Category = session.Category }, withHistory);
            session.Persisted = true;
            session.SavedBody = session.Body;
            session.SavedRevision = session.Revision;
            session.Failed = false;
            session.BodyTouched = false;
            if (!wasPersisted || withHistory)
            {
                session.HistoryRecorded = true;
                session.LastHistoryAt = DateTimeOffset.UtcNow;
            }
            return true;
        }
        catch (Exception error) when (NoteStore.IsStorageError(error))
        {
            session.Failed = true;
            return false;
        }
    }

    void EnqueueWrite(Session session)
    {
        // 历史时机（notes-mvp §4）：本次会话第一次修改正文，或停止输入超过数分钟后的下一次写盘。
        var withHistory = session.BodyTouched || session.LastHistoryAt is not { } last || DateTimeOffset.UtcNow - last >= HistoryIdle;
        if (withHistory) { session.BodyTouched = false; session.LastHistoryAt = DateTimeOffset.UtcNow; }
        var job = new WriteJob(session, session.Title, session.Body, session.Category, session.Revision, session.Epoch, withHistory);
        lock (gate)
        {
            if (disposed) return;
            if (writer == null)
            {
                writer = new Thread(WriteLoop) { IsBackground = true, Name = "pane-notes-writer" };
                writer.Start();
            }
        }
        writes.Add(job);
    }

    void WriteLoop()
    {
        try
        {
            foreach (var job in writes.GetConsumingEnumerable())
            {
                try
                {
                    var saved = !job.Session.Persisted
                        ? store.Create(job.Title, job.Body, job.Category, job.Session.Id)
                        : store.Save(new(job.Session.Id, job.Title, job.Body, default) { Category = job.Category }, job.WithHistory);
                    marshal(() =>
                    {
                        lock (gate)
                        {
                            var session = sessions.GetValueOrDefault(job.Session.Id);
                            if (session == null || job.Epoch < session.Epoch) return;
                            if (job.Revision >= session.SavedRevision)
                            {
                                var wasPersisted = session.Persisted;
                                session.Persisted = true;
                                session.SavedRevision = job.Revision;
                                session.Failed = false;
                                session.SavedBody = job.Body;
                                if (!wasPersisted || job.WithHistory)
                                {
                                    session.HistoryRecorded = true;
                                    session.LastHistoryAt = DateTimeOffset.UtcNow;
                                }
                                Saved?.Invoke(session);
                            }
                        }
                    });
                }
                catch (Exception error) when (NoteStore.IsStorageError(error))
                {
                    marshal(() =>
                    {
                        lock (gate)
                        {
                            var session = sessions.GetValueOrDefault(job.Session.Id);
                            if (session == null || job.Epoch < session.Epoch) return;
                            session.Failed = true;
                            SaveFailed?.Invoke(session);
                        }
                    });
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var session in sessions.Values) StopAutosave(session);
        }
        writes.CompleteAdding();
        writer?.Join(TimeSpan.FromSeconds(2));
        store.Changed -= StoreChanged;
    }
}
