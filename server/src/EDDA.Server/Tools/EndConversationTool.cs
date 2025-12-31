using EDDA.Server.Services.Llm;
using EDDA.Server.Services.Session;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Tools;

/// <summary>
/// Tool for ending the current voice conversation (deactivates after the current response).
/// </summary>
public sealed class EndConversationTool : LlmTool
{
    private readonly IVoiceSessionAccessor _sessions;
    private readonly ILogger<EndConversationTool>? _logger;

    public EndConversationTool(IVoiceSessionAccessor sessions, ILogger<EndConversationTool>? logger = null)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public override string Name => "end_conversation";

    public override string Description =>
        "End the current voice conversation. Use this when the user says they're done, wants to stop, or asks you to end the conversation. " +
        "This deactivates the session after you finish your current response.";

    protected override Task<ToolResult> ExecuteAsync(CancellationToken ct)
    {
        var session = _sessions.Current;

        if (session is null)
        {
            return Task.FromResult(ToolResult.Error(
                "No active voice session is available in this context. Ask the user to end the conversation verbally instead."));
        }

        if (!session.IsActive)
        {
            return Task.FromResult(ToolResult.Success(new
            {
                deactivation_requested = false,
                already_inactive = true
            }));
        }

        session.RequestDeactivation();
        _logger?.LogInformation("Tool requested session deactivation");

        return Task.FromResult(ToolResult.Success(new
        {
            deactivation_requested = true
        }));
    }
}

