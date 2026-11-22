using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace Pane.Services.Traffic;

internal enum TrafficApplicationSort { Total, Download, Upload, Name }
internal enum TrafficRankingSort { Total, Download, Upload }
internal enum TrafficSortColumn { Name, Download, Upload, RunDownload, RunUpload }

internal readonly record struct TrafficRankingQuery(string Keyword, bool Cumulative, TrafficRankingSort Sort, bool ActiveOnly = false,
    TrafficSortColumn? Column = null, bool Ascending = false)
{
    internal const int KeywordLimit = 160;
    internal bool HasFilter => ActiveOnly || Keyword.Length > 0;
}

internal readonly record struct TrafficHistoryQuery(DateTimeOffset From, DateTimeOffset To, int ResolutionSeconds,
    string? ProcessInstanceId = null, bool ByApplication = false, string? ApplicationKeyword = null,
    TrafficApplicationSort ApplicationSort = TrafficApplicationSort.Total)
{
    internal const int ApplicationKeywordLimit = 160;
    internal bool HasApplicationOptions => !string.IsNullOrEmpty(ApplicationKeyword) || ApplicationSort != TrafficApplicationSort.Total;
}

internal sealed class TrafficPipeProtocol(NamedPipeClientStream pipe) : IDisposable
{
    readonly byte[] response = new byte[65536];

    public static async Task<TrafficPipeProtocol> ConnectAsync(CancellationToken token, int? expectedProcessId = null)
    {
        var pipe = new NamedPipeClientStream(".", TrafficServiceIdentity.PipeName, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            try { await pipe.ConnectAsync(500, token).ConfigureAwait(false); }
            catch (UnauthorizedAccessException error) { throw new TrafficPipeAccessDeniedException(error); }
            catch (TimeoutException error) { throw new IOException("The traffic service pipe did not respond.", error); }
            TrafficServiceIdentity.Verify(pipe.SafePipeHandle, expectedProcessId);
            return new TrafficPipeProtocol(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> RequestAsync(string operation, CancellationToken token, bool? enabled = null,
        int? generation = null, string? collectorEpoch = null, int? limit = null, TrafficHistoryQuery? history = null,
        string? cursor = null, bool? confirm = null, (string InstanceId, string Kind)? connections = null,
        bool firstPageOnly = false, TrafficRankingQuery? ranking = null)
    {
        var id = Guid.NewGuid().ToString("N");
        using var request = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(request))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("id", id);
            writer.WriteString("op", operation);
            writer.WriteString("scope", "own");
            if (enabled is { } flag) writer.WriteBoolean("enabled", flag);
            if (generation is { } current) writer.WriteNumber("generation", current);
            if (collectorEpoch != null) writer.WriteString("collectorEpoch", collectorEpoch);
            if (limit is { } rows) writer.WriteNumber("limit", rows);
            if (firstPageOnly) writer.WriteBoolean("paginate", false);
            if (ranking is { } ranked)
            {
                if (ranked.Keyword.Length > 0) writer.WriteString("keyword", ranked.Keyword);
                writer.WriteString("rankingMode", ranked.Cumulative ? "app-run" : "rate");
                writer.WriteString("rankingSort", ranked.Sort switch
                {
                    TrafficRankingSort.Download => "download",
                    TrafficRankingSort.Upload => "upload",
                    _ => "total"
                });
            }
            if (history is { } range)
            {
                writer.WriteString("from", range.From);
                writer.WriteString("to", range.To);
                writer.WriteNumber("resolutionSec", range.ResolutionSeconds);
                if (range.ProcessInstanceId is { } processId) writer.WriteString("processInstanceId", processId);
                if (range.ByApplication)
                {
                    writer.WriteString("groupBy", "application");
                    if (!string.IsNullOrEmpty(range.ApplicationKeyword)) writer.WriteString("applicationKeyword", range.ApplicationKeyword);
                    if (range.ApplicationSort != TrafficApplicationSort.Total) writer.WriteString("applicationSort", range.ApplicationSort switch
                    {
                        TrafficApplicationSort.Download => "download",
                        TrafficApplicationSort.Upload => "upload",
                        TrafficApplicationSort.Name => "name",
                        _ => throw new ArgumentOutOfRangeException(nameof(range.ApplicationSort))
                    });
                }
            }
            if (cursor != null) writer.WriteString("cursor", cursor);
            if (confirm is { } confirmed) writer.WriteBoolean("confirm", confirmed);
            if (connections is { } endpointQuery)
            {
                writer.WriteString("processInstanceId", endpointQuery.InstanceId);
                writer.WriteString("kind", endpointQuery.Kind);
            }
            writer.WriteEndObject();
        }
        request.WriteByte((byte)'\n');
        await pipe.WriteAsync(request.GetBuffer().AsMemory(0, (int)request.Length), token).ConfigureAwait(false);

        for (var length = 0; length < response.Length;)
        {
            var count = await pipe.ReadAsync(response.AsMemory(length), token).ConfigureAwait(false);
            if (count == 0) break;
            var newline = Array.IndexOf(response, (byte)'\n', length, count);
            length += count;
            if (newline < 0) continue;
            if (newline != length - 1) break;
            using var document = JsonDocument.Parse(response.AsMemory(0, newline));
            var message = document.RootElement;
            if (message.GetProperty("v").GetInt32() != 1 || message.GetProperty("id").GetString() != id)
                throw new InvalidDataException("Unexpected traffic response.");
            if (!message.GetProperty("ok").GetBoolean()) throw new TrafficRequestException(message.GetProperty("error").GetString());
            return message.GetProperty("data").Clone();
        }
        throw new InvalidDataException("Missing or oversized traffic response.");
    }

    public void Dispose() => pipe.Dispose();
}

internal sealed class TrafficPipeAccessDeniedException(UnauthorizedAccessException error)
    : IOException("Access to the traffic service pipe was denied.", error);

internal sealed class TrafficRequestException(string? code) : IOException("The traffic service rejected the request.")
{
    public string? Code { get; } = code;
}
