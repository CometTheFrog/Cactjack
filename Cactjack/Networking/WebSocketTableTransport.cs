using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cactjack.Networking;

public sealed record CreatedRelayRoom(string Code, string HostToken);

public sealed class WebSocketTableTransport : ITableTransport
{
    public const string RelayBaseUrl = "https://cactjack-relay.cactjack-relay.workers.dev";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private Task? receiveTask;

    public event Action<TableCommand>? CommandReceived;
    public event Action<TableSnapshot>? SnapshotReceived;
    public event Action<string>? StatusChanged;

    public WebSocketState State => socket.State;

    public static async Task<CreatedRelayRoom> CreateRoomAsync(CancellationToken token = default)
    {
        using var response = await HttpClient.PostAsync($"{RelayBaseUrl}/create", null, token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(token);
        return JsonSerializer.Deserialize<CreatedRelayRoom>(json, JsonOptions)
               ?? throw new InvalidOperationException("Relay returned an invalid room response.");
    }

    public async Task ConnectHostAsync(CreatedRelayRoom room, string clientId, CancellationToken token = default)
    {
        var uri = BuildWebSocketUri(room.Code, "host", clientId, room.HostToken);
        await ConnectAsync(uri, token);
    }

    public async Task ConnectPlayerAsync(string code, string clientId, CancellationToken token = default)
    {
        var uri = BuildWebSocketUri(code, "player", clientId, null);
        await ConnectAsync(uri, token);
    }

    public void Send(TableCommand command) => _ = SendJsonAsync(command, cancellation.Token);
    public void Publish(TableSnapshot snapshot) => _ = SendJsonAsync(snapshot, cancellation.Token);

    public void Dispose()
    {
        cancellation.Cancel();
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "plugin_disposed", CancellationToken.None).GetAwaiter().GetResult(); }
            catch { }
        }
        socket.Dispose();
        sendLock.Dispose();
        cancellation.Dispose();
    }

    private async Task ConnectAsync(Uri uri, CancellationToken token)
    {
        StatusChanged?.Invoke("Connecting...");
        await socket.ConnectAsync(uri, token);
        StatusChanged?.Invoke("Connected");
        receiveTask = ReceiveLoopAsync(cancellation.Token);
    }

    private async Task SendJsonAsync<T>(T value, CancellationToken token)
    {
        if (socket.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await sendLock.WaitAsync(token);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        { StatusChanged?.Invoke(exception is OperationCanceledException ? "Disconnected" : $"Network error: {exception.Message}"); }
        finally { sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[65_536];
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(count), token);
                    count += result.Count;
                    if (count == buffer.Length && !result.EndOfMessage) throw new WebSocketException("Relay message exceeded 64 KB.");
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                var json = Encoding.UTF8.GetString(buffer, 0, count);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("protocolVersion", out _))
                {
                    var snapshot = JsonSerializer.Deserialize<TableSnapshot>(json, JsonOptions);
                    if (snapshot is not null) SnapshotReceived?.Invoke(snapshot);
                }
                else if (document.RootElement.TryGetProperty("type", out _))
                {
                    CommandReceived?.Invoke(JsonSerializer.Deserialize<TableCommand>(json, JsonOptions));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { StatusChanged?.Invoke($"Network error: {exception.Message}"); }
        finally { StatusChanged?.Invoke("Disconnected"); }
    }

    private static Uri BuildWebSocketUri(string code, string role, string clientId, string? token)
    {
        var baseUri = RelayBaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
        var query = $"role={Uri.EscapeDataString(role)}&clientId={Uri.EscapeDataString(clientId)}";
        if (!string.IsNullOrEmpty(token)) query += $"&token={Uri.EscapeDataString(token)}";
        return new Uri($"{baseUri}/room/{Uri.EscapeDataString(code.Trim().ToUpperInvariant())}?{query}");
    }
}
