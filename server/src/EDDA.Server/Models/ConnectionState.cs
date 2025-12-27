namespace EDDA.Server.Models;

/// <summary>
/// State machine states for WebSocket connection handling.
/// </summary>
public enum ConnectionState
{
    /// <summary>No active speech, waiting for audio.</summary>
    Idle,
    
    /// <summary>Currently receiving audio chunks from client.</summary>
    ReceivingSpeech,
    
    /// <summary>Speech ended, waiting to see if more speech follows before responding.</summary>
    WaitingForMore
}

