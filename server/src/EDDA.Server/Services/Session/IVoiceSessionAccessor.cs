using EDDA.Server.Models;

namespace EDDA.Server.Services.Session;

/// <summary>
/// Accessor for the current <see cref="VoiceSession"/> in the async execution context.
/// This is how LLM tools can interact with the active session without static globals.
/// </summary>
public interface IVoiceSessionAccessor
{
    /// <summary>
    /// The current session, if one is active in this async context.
    /// </summary>
    VoiceSession? Current { get; }

    /// <summary>
    /// Set the current session for the lifetime of the returned scope.
    /// </summary>
    IDisposable Use(VoiceSession session);
}

