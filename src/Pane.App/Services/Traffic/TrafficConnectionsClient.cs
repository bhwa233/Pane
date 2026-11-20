using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text.Json;

namespace Pane.Services.Traffic;

internal enum TrafficConnectionsState { Available, NotInstalled, Unavailable, AccessDenied, ProcessGone, Disabled }

internal sealed record TrafficEndpoint(string Local, string? Remote, TcpState? State);

internal sealed record TrafficConnectionsResult(TrafficConnectionsState State, IReadOnlyList<TrafficEndpoint> Rows, bool Truncated = false);

internal static class TrafficConnectionsClient
{
    internal const string Tcp = "tcp", Udp = "udp-local";
    const int Limit = 64;

    internal static async Task<TrafficConnectionsResult> ReadAsync(TrafficProcessIdentity process, string kind, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!TrafficServiceIdentity.IsInstalled()) return new(TrafficConnectionsState.NotInstalled, []);
            using var pipe = await TrafficPipeProtocol.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var response = await pipe.RequestAsync("connections", deadline.Token, limit: Limit,
                connections: (process.InstanceId, kind)).ConfigureAwait(false);
            return Parse(response, process, kind);
        }
        catch (TrafficRequestException error) when (error.Code is "process-not-visible" or "scope-disabled")
        {
            return new(error.Code == "scope-disabled" ? TrafficConnectionsState.Disabled : TrafficConnectionsState.ProcessGone, []);
        }
        catch (TrafficPipeAccessDeniedException) { return new(TrafficConnectionsState.AccessDenied, []); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(TrafficConnectionsState.Unavailable, []); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or
            JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        {
            return new(TrafficConnectionsState.Unavailable, []);
        }
    }

    internal static TrafficConnectionsResult Parse(JsonElement response, TrafficProcessIdentity process, string kind)
    {
        if (kind is not (Tcp or Udp) || response.GetProperty("source").GetString() != "ip-helper" ||
            response.GetProperty("scope").GetString() != "own" || response.GetProperty("kind").GetString() != kind ||
            response.GetProperty("processInstanceId").GetString() != process.InstanceId) throw new InvalidDataException("Unexpected connection response.");
        var endpoints = response.GetProperty("endpoints");
        if (endpoints.GetArrayLength() > Limit) throw new InvalidDataException("Too many connection rows.");
        var rows = new List<TrafficEndpoint>();
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            var tcp = kind == Tcp;
            if (endpoint.GetProperty("pid").GetInt32() != process.Pid || endpoint.GetProperty("protocol").GetString() != (tcp ? "tcp" : "udp"))
                throw new InvalidDataException("Unexpected connection owner or protocol.");
            var family = endpoint.GetProperty("addressFamily").GetString() switch
            {
                "ipv4" => AddressFamily.InterNetwork,
                "ipv6" => AddressFamily.InterNetworkV6,
                _ => throw new InvalidDataException("Unexpected address family.")
            };
            var local = ReadEndpoint(endpoint, "localAddress", "localPort", family);
            if (!tcp) { rows.Add(new(local, null, null)); continue; }
            if (!Enum.TryParse<TcpState>(endpoint.GetProperty("state").GetString(), out var state) || !Enum.IsDefined(state)) state = TcpState.Unknown;
            var remote = ReadEndpoint(endpoint, "remoteAddress", "remotePort", family);
            rows.Add(new(local, state == TcpState.Listen ? null : remote, state));
        }
        return new(TrafficConnectionsState.Available, rows, response.GetProperty("truncated").GetBoolean());
    }

    static string ReadEndpoint(JsonElement endpoint, string addressName, string portName, AddressFamily family)
    {
        if (!IPAddress.TryParse(endpoint.GetProperty(addressName).GetString(), out var address) || address.AddressFamily != family ||
            !endpoint.GetProperty(portName).TryGetInt32(out var port) || port is < 0 or > 65535) throw new InvalidDataException("Invalid connection endpoint.");
        return new IPEndPoint(address, port).ToString();
    }
}
