using System.Text;
using System.Text.Json;
using EDDA.Server.Services.Llm;

namespace EDDA.Server.Agent;

/// <summary>
/// Accumulates streaming tool call deltas into a complete ToolCall.
/// 
/// When streaming, the LLM sends tool calls in pieces:
/// - First chunk: id + function name
/// - Subsequent chunks: argument fragments
/// 
/// This builder collects all pieces and produces a complete ToolCall
/// when the stream finishes.
/// </summary>
public class ToolCallBuilder
{
    private string? _id;
    private string? _name;
    private string? _thoughtSignature;
    private readonly StringBuilder _arguments = new();

    /// <summary>
    /// Whether we have at least the tool call ID.
    /// </summary>
    public bool HasId => _id != null;

    /// <summary>
    /// Whether we have both ID and name (minimum for a valid tool call).
    /// </summary>
    public bool IsValid => _id != null && _name != null;

    /// <summary>
    /// The tool name (for logging before the call is complete).
    /// </summary>
    public string? Name => _name;
    
    /// <summary>
    /// The thought signature (for logging/debugging).
    /// </summary>
    public string? ThoughtSignature => _thoughtSignature;

    /// <summary>
    /// Apply a streaming delta to this builder.
    /// </summary>
    public void Apply(ToolCallDelta delta)
    {
        if (delta.Id != null)
            _id = delta.Id;

        if (delta.Name != null)
            _name = delta.Name;

        if (delta.ArgumentsDelta != null)
            _arguments.Append(delta.ArgumentsDelta);
        
        // Capture thought_signature - required for Gemini 3 multi-turn tool calls
        if (delta.ThoughtSignature != null)
            _thoughtSignature = delta.ThoughtSignature;
    }

    /// <summary>
    /// Build the complete ToolCall from accumulated deltas.
    /// </summary>
    /// <exception cref="InvalidOperationException">If required fields are missing.</exception>
    public ToolCall Build()
    {
        if (_id == null)
            throw new InvalidOperationException("Tool call ID not received");

        if (_name == null)
            throw new InvalidOperationException("Tool call name not received");

        var argsJson = _arguments.Length > 0
            ? _arguments.ToString()
            : "{}"; // Default to empty object if no arguments

        JsonElement arguments;
        try
        {
            arguments = JsonDocument.Parse(argsJson).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse tool arguments for {_name}: {ex.Message}", ex);
        }

        return new ToolCall
        {
            Id = _id,
            Name = _name,
            Arguments = arguments,
            ThoughtSignature = _thoughtSignature
        };
    }

    /// <summary>
    /// Reset the builder for reuse.
    /// </summary>
    public void Reset()
    {
        _id = null;
        _name = null;
        _thoughtSignature = null;
        _arguments.Clear();
    }
}
