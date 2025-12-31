using System.Threading;
using EDDA.Server.Models;

namespace EDDA.Server.Services.Session;

/// <summary>
/// AsyncLocal-based implementation of <see cref="IVoiceSessionAccessor"/>.
/// </summary>
public sealed class VoiceSessionAccessor : IVoiceSessionAccessor
{
    private static readonly AsyncLocal<VoiceSession?> CurrentSession = new();

    public VoiceSession? Current => CurrentSession.Value;

    public IDisposable Use(VoiceSession session)
    {
        var prior = CurrentSession.Value;
        CurrentSession.Value = session;
        return new PopScope(prior);
    }

    private sealed class PopScope(VoiceSession? prior) : IDisposable
    {
        public void Dispose()
        {
            CurrentSession.Value = prior;
        }
    }
}

