namespace EDDA.Server.Models;

/// <summary>
/// Voice input pipeline states (STT flow).
/// Tracks the state of incoming audio from the user.
/// </summary>
public enum InputState
{
    /// <summary>
    /// No active speech. Waiting for audio input.
    /// </summary>
    Idle,
    
    /// <summary>
    /// Currently receiving audio chunks from the client.
    /// Transitions to WaitingForMore when speech ends.
    /// </summary>
    Listening,
    
    /// <summary>
    /// Speech segment ended. Waiting to see if user speaks again
    /// before triggering a response. Allows natural pauses in speech.
    /// </summary>
    WaitingForMore
}
