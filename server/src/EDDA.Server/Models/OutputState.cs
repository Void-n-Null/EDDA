namespace EDDA.Server.Models;

/// <summary>
/// Response generation pipeline state (LLM side).
/// Tracks what the AI is currently doing.
/// </summary>
/// <remarks>
/// This is SEPARATE from playback state. Generation and playback are independent:
/// - The LLM can be generating sentence N+1 while sentence N is playing
/// - Audio playback is managed client-side (queue of completed sentences)
/// - Server fires TTS audio as sentences complete; client plays them sequentially
/// 
/// The server doesn't track playback state — it just sends audio and the client
/// manages its own queue. This allows pipelining: generate → TTS → send → repeat,
/// while client plays audio independently.
/// </remarks>
public enum OutputState
{
    /// <summary>
    /// No active response generation.
    /// </summary>
    Idle,
    
    /// <summary>
    /// LLM is streaming text. Completed sentences are sent to TTS immediately.
    /// </summary>
    Generating,
    
    /// <summary>
    /// AI requested a tool call. Waiting for tool result before continuing.
    /// Server should trigger loading audio on client during this state.
    /// </summary>
    ToolCall
}
