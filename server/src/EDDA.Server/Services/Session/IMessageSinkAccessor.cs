using EDDA.Server.Handlers;

namespace EDDA.Server.Services.Session;

/// <summary>
/// Accessor for the current <see cref="WebSocketMessageSink"/> in the async execution context.
/// This is how LLM tools can send messages to the client without coupling to WebSocket directly.
/// </summary>
public interface IMessageSinkAccessor
{
    /// <summary>
    /// The current message sink, if one is active in this async context.
    /// </summary>
    WebSocketMessageSink? Current { get; }

    /// <summary>
    /// Set the current message sink for the lifetime of the returned scope.
    /// </summary>
    IDisposable Use(WebSocketMessageSink sink);
}
