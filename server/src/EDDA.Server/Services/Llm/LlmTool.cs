using System.Text.Json;

namespace EDDA.Server.Services.Llm;

/// <summary>
/// Non-generic base interface for tool discovery and invocation.
/// </summary>
public interface ILlmTool
{
    /// <summary>
    /// Unique name of the tool (used in LLM function calling).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Description of what the tool does (shown to LLM).
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON schema for the tool's parameters.
    /// </summary>
    JsonElement GetParameterSchema();

    /// <summary>
    /// Type of the parameters class.
    /// </summary>
    Type ParameterType { get; }

    /// <summary>
    /// Execute the tool with raw JSON arguments.
    /// </summary>
    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default);
}

/// <summary>
/// Base class for LLM tools with strongly-typed parameters.
/// Inherit from this class to create a tool that can be discovered
/// and invoked by the LLM.
/// </summary>
/// <typeparam name="TParameters">
/// Parameter class. Properties become the tool's input schema.
/// Use System.ComponentModel.DescriptionAttribute on properties for descriptions.
/// Nullable properties are optional; non-nullable are required.
/// </typeparam>
public abstract class LlmTool<TParameters> : ILlmTool where TParameters : class, new()
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public Type ParameterType => typeof(TParameters);

    /// <summary>
    /// JSON serializer options for parameter deserialization.
    /// </summary>
    protected virtual JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public virtual JsonElement GetParameterSchema()
    {
        return JsonSchemaGenerator.Generate<TParameters>();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        TParameters? parameters;

        try
        {
            parameters = arguments.Deserialize<TParameters>(JsonOptions);

            if (parameters is null)
            {
                return ToolResult.InvalidInput("Failed to deserialize parameters");
            }
        }
        catch (JsonException ex)
        {
            return ToolResult.InvalidInput($"JSON parsing error: {ex.Message}");
        }

        return await ExecuteAsync(parameters, ct);
    }

    /// <summary>
    /// Execute the tool with strongly-typed parameters.
    /// Override this method to implement the tool's logic.
    /// </summary>
    protected abstract Task<ToolResult> ExecuteAsync(TParameters parameters, CancellationToken ct);
}

/// <summary>
/// Base class for LLM tools that take no parameters.
/// </summary>
public abstract class LlmTool : ILlmTool
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public Type ParameterType => typeof(EmptyParameters);

    /// <inheritdoc />
    public JsonElement GetParameterSchema()
    {
        // Empty object schema
        return JsonDocument.Parse("""{"type": "object", "properties": {}}""").RootElement.Clone();
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        return ExecuteAsync(ct);
    }

    /// <summary>
    /// Execute the tool.
    /// </summary>
    protected abstract Task<ToolResult> ExecuteAsync(CancellationToken ct);

    /// <summary>
    /// Empty parameters placeholder.
    /// </summary>
    private sealed class EmptyParameters { }
}
