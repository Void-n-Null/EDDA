namespace EDDA.Server.Services;

/// <summary>
/// Abstraction for sending messages to a client.
/// Decouples response pipeline from transport (WebSocket, SSE, etc.)
/// </summary>
public interface IMessageSink
{
    /// <summary>
    /// Send a payload to the client. Payload will be JSON-serialized.
    /// </summary>
    ValueTask SendAsync(object payload, CancellationToken ct = default);
    
    /// <summary>
    /// Whether the underlying connection is still open.
    /// </summary>
    bool IsConnected { get; }
}
