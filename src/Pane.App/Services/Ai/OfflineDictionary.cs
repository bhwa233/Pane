using System.Runtime.InteropServices;
using System.Text;

namespace Pane.Services.Ai;

internal sealed record DictionaryEntry(string Word, string Phonetic, string Translation);

internal static partial class OfflineDictionary
{
    const int OpenReadOnly = 1, Row = 100, Done = 101;
    static readonly nint Transient = -1;

    internal static string? FindPath(string dataDirectory, string appDirectory)
    {
        var installed = Path.Combine(dataDirectory, DataFolder, DatabaseName);
        if (File.Exists(installed)) return installed;
        var bundled = Path.Combine(appDirectory, DataFolder, DatabaseName);
        return File.Exists(bundled) ? bundled : null;
    }

    internal static IReadOnlyList<DictionaryEntry> Lookup(string path, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (text.Length is 0 or > 512 || text.Contains('\0')) return [];
        var query = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Trim();
        if (query.Length is 0 or > 512) return [];
        int result = Open(path, out var database, OpenReadOnly, 0);
        try
        {
            Check(result);
            nint statement = 0;
            try
            {
                Check(Prepare(database, "SELECT value FROM metadata WHERE key = 'schemaVersion'", -1, out statement, 0));
                if (Step(statement) != Row || Text(statement, 0) != "1") throw new InvalidDataException();
            }
            finally { if (statement != 0) FinalizeStatement(statement); }

            var entries = new List<DictionaryEntry>(8);
            const string sql = """
                WITH matches(id, priority) AS (
                    SELECT id, 0 FROM entries WHERE normalized = ?1
                    UNION ALL SELECT entry_id, 1 FROM forms WHERE form = ?1
                    UNION ALL SELECT id, 2 FROM entries WHERE normalized >= ?1 AND normalized < ?2
                )
                SELECT e.word, e.phonetic, e.translation
                FROM matches m JOIN entries e ON e.id = m.id
                GROUP BY e.id ORDER BY min(m.priority), e.rank, e.normalized, e.id LIMIT 8
                """;
            statement = 0;
            try
            {
                Check(Prepare(database, sql, -1, out statement, 0));
                Check(Bind(statement, 1, query, -1, Transient));
                Check(Bind(statement, 2, query.Length >= 2 ? query + '\uffff' : query, -1, Transient));
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    result = Step(statement);
                    if (result == Done) return entries;
                    if (result != Row) throw new InvalidDataException();
                    entries.Add(new(Text(statement, 0), Text(statement, 1), Text(statement, 2).Replace("\\n", "\n", StringComparison.Ordinal)));
                }
            }
            finally { if (statement != 0) FinalizeStatement(statement); }
        }
        finally { if (database != 0) Close(database); }
    }

    static void Check(int result)
    {
        if (result != 0) throw new InvalidDataException();
    }

    static string Text(nint statement, int column) => Marshal.PtrToStringUTF8(ColumnText(statement, column)) ?? "";

    const string Library = "winsqlite3.dll";
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport(Library, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, out nint database, int flags, nint vfs);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport(Library, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Prepare(nint database, string sql, int bytes, out nint statement, nint tail);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport(Library, EntryPoint = "sqlite3_bind_text", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Bind(nint statement, int index, string text, int bytes, nint destroy);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport(Library, EntryPoint = "sqlite3_step")]
    private static partial int Step(nint statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport(Library, EntryPoint = "sqlite3_column_text")]
    private static partial nint ColumnText(nint statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport(Library, EntryPoint = "sqlite3_finalize")]
    private static partial int FinalizeStatement(nint statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport(Library, EntryPoint = "sqlite3_close")]
    private static partial int Close(nint database);
}
