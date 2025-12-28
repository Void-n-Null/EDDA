namespace EDDA.Server.Models;

/// <summary>
/// Voice output pipeline states (LLM + TTS flow).
/// Tracks the state of response generation and playback.
/// </summary>
/// <remarks>
/// Future states to add:
/// - Generating: LLM is streaming text, buffering sentences
/// - ToolCall: AI requested a tool, playing loading audio
/// - Speaking: TTS audio is streaming to the client
/// </remarks>
public enum OutputState
{
    /// <summary>
    /// No active response generation or playback.
    /// </summary>
    Idle
    
    // Future:
    // Generating,
    // ToolCall,
    // Speaking
}
