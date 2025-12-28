using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EDDA.Server.Services;

namespace EDDA.Server.Handlers;

/// <summary>
/// IMessageSink implementation backed by a WebSocket.
/// </summary>
public sealed class WebSocketMessageSink(WebSocket socket) : IMessageSink
{
    public bool IsConnected => socket.State == WebSocketState.Open;
    public async ValueTask SendAsync(object payload, CancellationToken ct = default)
    {
        if (!IsConnected) return;
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
    }
}
