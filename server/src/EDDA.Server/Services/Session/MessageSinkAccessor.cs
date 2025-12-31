using EDDA.Server.Handlers;

namespace EDDA.Server.Services.Session;

/// <summary>
/// AsyncLocal-based implementation of <see cref="IMessageSinkAccessor"/>.
/// </summary>
public sealed class MessageSinkAccessor : IMessageSinkAccessor
{
    private static readonly AsyncLocal<WebSocketMessageSink?> CurrentSink = new();

    public WebSocketMessageSink? Current => CurrentSink.Value;

    public IDisposable Use(WebSocketMessageSink sink)
    {
        var prior = CurrentSink.Value;
        CurrentSink.Value = sink;
        return new PopScope(prior);
    }

    private sealed class PopScope(WebSocketMessageSink? prior) : IDisposable
    {
        public void Dispose()
        {
            CurrentSink.Value = prior;
        }
    }
}
