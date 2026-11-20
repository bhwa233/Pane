using Pane.Contracts;
namespace Pane.Services.Modules;

// 视图保留这个稳定代理，模块重建后不会继续引用已释放的历史服务。
internal sealed class ClipboardBridge : IClipboardHistory
{
    IClipboardHistory? target;
    public void Attach(IClipboardHistory? value)
    {
        if (target != null) target.Changed -= OnChanged;
        target = value;
        if (target != null) target.Changed += OnChanged;
        OnChanged(this, EventArgs.Empty);
    }
    void OnChanged(object? sender, EventArgs args) => Changed?.Invoke(this, args);
    public event EventHandler? Changed;
    public string? StorageProblem => target?.StorageProblem;
    public IReadOnlyList<ClipEntry> Snapshot(bool includeArchived = false) => target?.Snapshot(includeArchived) ?? [];
    public Task PasteAsync(string id, bool plainText, nint window, CancellationToken token) =>
        target?.PasteAsync(id, plainText, window, token) ?? Task.CompletedTask;
    public Task<ClipMergeResult> MergeAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        target?.MergeAsync(ids, cancellationToken) ?? Task.FromResult(ClipMergeResult.Unavailable);
    public Task<ClipGalleryImportResult> ImportGalleryAsync(IReadOnlyList<string> paths, string collection, CancellationToken cancellationToken) =>
        target?.ImportGalleryAsync(paths, collection, cancellationToken) ?? Task.FromResult(new ClipGalleryImportResult(0, 0, paths.Count));
    public ClipTextCopyResult SaveTextCopy(string id, string text) => target?.SaveTextCopy(id, text) ?? ClipTextCopyResult.Unavailable;
    public Task ExportFileAsync(string id, string path, CancellationToken cancellationToken) =>
        target?.ExportFileAsync(id, path, cancellationToken) ?? Task.FromException(new NotSupportedException());
    public void SetPinned(string id, bool pinned) => target?.SetPinned(id, pinned);
    public bool SetFavorite(string id, bool favorite) => target?.SetFavorite(id, favorite) ?? false;
    public bool SetAnnotation(string id, ClipAnnotation? annotation) => target?.SetAnnotation(id, annotation) ?? false;
    public bool SetDetails(string id, ClipAnnotation? annotation, IReadOnlyList<string>? collections) => target?.SetDetails(id, annotation, collections) ?? false;
    public void Delete(string id) => target?.Delete(id);
    public void ClearUnpinned() => target?.ClearUnpinned();
    public ClipboardCleanupPreview PreviewCleanup() => target?.PreviewCleanup() ?? new([], 0);
    public int ConfirmCleanup(ClipboardCleanupPreview preview) => target?.ConfirmCleanup(preview) ?? 0;
}
