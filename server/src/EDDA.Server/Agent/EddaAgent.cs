using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using EDDA.Server.Agent.Context;
using EDDA.Server.Agent.Prompts;
using EDDA.Server.Models;
using EDDA.Server.Services.Llm;
using EDDA.Server.Services.Memory;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Agent;

/// <summary>
/// Main agent implementation for EDDA voice assistant.
/// 
/// Handles:
/// - Streaming LLM responses with incremental sentence detection
/// - Tool calling with automatic execution and continuation
/// - Context injection via pluggable providers
/// - Per-turn memory search for RAG
/// - Conversation state management
/// </summary>
public partial class EddaAgent : IAgent
{
    private readonly IOpenRouterService _llm;
    private readonly ToolDiscovery _tools;
    private readonly ToolExecutor _toolExecutor;
    private readonly ContextBuilder _contextBuilder;
    private readonly IConversationMemory? _memory;
    private readonly ILogger<EddaAgent> _logger;

    private readonly string _systemPromptTemplate;
    private readonly ChatOptions _defaultOptions;

    private const int MaxToolRounds = 10;

    public EddaAgent(
        IOpenRouterService llm,
        ToolDiscovery tools,
        ToolExecutor toolExecutor,
        ContextBuilder contextBuilder,
        IConversationMemory? memory,
        ILogger<EddaAgent> logger)
    {
        _llm = llm;
        _tools = tools;
        _toolExecutor = toolExecutor;
        _contextBuilder = contextBuilder;
        _memory = memory;
        _logger = logger;

        _systemPromptTemplate = PromptLoader.Load("system.md");

        var toolDefs = _tools.GetOpenAiToolDefinitions().ToList();
        _defaultOptions = new ChatOptions
        {
            Tools = toolDefs.Count > 0 ? toolDefs : null
        };

        _logger.LogDebug(
            "EddaAgent initialized with {ToolCount} tools, memory: {HasMemory}",
            toolDefs.Count,
            memory?.IsInitialized ?? false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentChunk> ProcessStreamAsync(
        Conversation conversation,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Build system prompt on first message
        if (!conversation.HasSystemPrompt)
        {
            var contextRequest = new ContextRequest
            {
                Now = DateTime.Now,
                UserMessage = userMessage,
                Conversation = conversation
            };

            var systemPrompt = await _contextBuilder.BuildAsync(
                _systemPromptTemplate,
                contextRequest,
                ct);

            conversation.SetSystemPrompt(systemPrompt);

            _logger.LogDebug(
                "System prompt built: {Length} chars",
                systemPrompt.Length);
        }

        // Search memory for relevant context on EVERY turn
        var memoryContext = await SearchMemoryAsync(userMessage, ct);
        
        // Build user message with memory context if available
        string finalUserMessage;
        if (!string.IsNullOrEmpty(memoryContext))
        {
            finalUserMessage = $"[Relevant memories from past conversations]\n{memoryContext}\n\n[Current message]\n{userMessage}";
            _logger.LogInformation("AGENT: Injecting memory context ({Length} chars)", memoryContext.Length);
        }
        else
        {
            finalUserMessage = userMessage;
        }

        // Add user message (with or without memory context)
        conversation.AddUserMessage(finalUserMessage);

        _logger.LogInformation(
            "AGENT: Processing turn {Turn}: \"{Message}\"",
            conversation.TurnCount,
            userMessage.Length > 60 ? userMessage[..60] + "..." : userMessage);

        // Stream with tool calling loop
        var toolRound = 0;
        
        _logger.LogInformation("AGENT: Entering tool loop (MaxRounds={Max})", MaxToolRounds);

        while (toolRound < MaxToolRounds)
        {
            toolRound++;
            _logger.LogInformation("AGENT: Starting round {Round}", toolRound);

            var sentenceBuffer = new StringBuilder();
            var contentBuffer = new StringBuilder();
            var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();
            var reasoningDetails = new List<ReasoningDetail>();
            var hasToolCalls = false;

            _logger.LogInformation("AGENT: Calling LLM stream...");
            
            var receivedAnyChunks = false;
            
            // Stream response
            await foreach (var chunk in _llm.ChatStreamAsync(
                conversation.Messages,
                _defaultOptions,
                ct))
            {
                receivedAnyChunks = true;
                
                // Accumulate reasoning_details (required for Gemini 3 / Claude / OpenAI reasoning models)
                if (chunk.ReasoningDetails is { Count: > 0 })
                {
                    reasoningDetails.AddRange(chunk.ReasoningDetails);
                    _logger.LogInformation(
                        "AGENT: Captured {Count} reasoning_details (total: {Total})",
                        chunk.ReasoningDetails.Count, reasoningDetails.Count);
                }
                
                // Handle content streaming
                if (!string.IsNullOrEmpty(chunk.ContentDelta))
                {
                    sentenceBuffer.Append(chunk.ContentDelta);
                    contentBuffer.Append(chunk.ContentDelta);

                    // Yield complete sentences immediately
                    while (TryExtractSentence(sentenceBuffer, out var sentence))
                    {
                        _logger.LogDebug("SENTENCE: \"{Sentence}\"", sentence);
                        yield return AgentChunk.Sentence(sentence);
                    }
                }

                // Handle tool call streaming
                if (chunk.ToolCallDeltas is { Count: > 0 })
                {
                    hasToolCalls = true;

                    foreach (var delta in chunk.ToolCallDeltas)
                    {
                        _logger.LogDebug(
                            "TOOL DELTA [idx={Index}]: id={Id}, name={Name}, args_chunk={Args}",
                            delta.Index,
                            delta.Id ?? "(null)",
                            delta.Name ?? "(null)",
                            delta.ArgumentsDelta?.Length > 50 
                                ? delta.ArgumentsDelta[..50] + "..." 
                                : delta.ArgumentsDelta ?? "(null)");
                        
                        if (!toolCallBuilders.TryGetValue(delta.Index, out var builder))
                        {
                            builder = new ToolCallBuilder();
                            toolCallBuilders[delta.Index] = builder;
                            _logger.LogDebug("TOOL DELTA: Created new builder for index {Index}", delta.Index);
                        }

                        builder.Apply(delta);
                    }
                }

                // Stream complete
                if (chunk.IsFinal)
                {
                    _logger.LogInformation(
                        "AGENT: LLM stream ended (round {Round}). FinishReason={Reason}, HasToolCalls={HasTools}, Content={ContentLen} chars",
                        toolRound,
                        chunk.FinishReason ?? "(null)",
                        hasToolCalls,
                        contentBuffer.Length);
                    break;
                }
            }
            
            // Check if stream completed without any chunks
            if (!receivedAnyChunks)
            {
                _logger.LogError("AGENT: Stream returned ZERO chunks in round {Round}! Aborting.", toolRound);
                yield return AgentChunk.Complete();
                yield break;
            }

            // Log the full content we received
            var fullContent = contentBuffer.ToString();
            _logger.LogInformation(
                "AGENT: LLM returned {ContentLen} chars: \"{Preview}\"",
                fullContent.Length,
                fullContent.Length > 100 ? fullContent[..100] + "..." : fullContent);

            // Yield any remaining content in buffer
            var remainder = sentenceBuffer.ToString().Trim();
            if (!string.IsNullOrEmpty(remainder))
            {
                _logger.LogInformation("AGENT: Yielding remainder: \"{Sentence}\"", remainder);
                yield return AgentChunk.Sentence(remainder);
            }

            // If no tool calls, we're done
            if (!hasToolCalls)
            {
                var content = contentBuffer.ToString();
                conversation.AddAssistantMessage(content);

                _logger.LogInformation(
                    "AGENT: Response complete ({Chars} chars, {Rounds} round(s))",
                    content.Length,
                    toolRound);

                yield return AgentChunk.Complete();
                yield break;
            }

            // Log builder states before building
            foreach (var (idx, builder) in toolCallBuilders)
            {
                _logger.LogDebug(
                    "TOOL BUILDER [idx={Index}]: IsValid={Valid}, Name={Name}",
                    idx, builder.IsValid, builder.Name ?? "(null)");
            }
            
            // Build and execute tool calls
            var toolCalls = new List<ToolCall>();
            foreach (var (idx, builder) in toolCallBuilders)
            {
                if (!builder.IsValid)
                {
                    _logger.LogWarning(
                        "TOOL BUILDER [idx={Index}]: Skipping invalid builder (Name={Name})",
                        idx, builder.Name ?? "(null)");
                    continue;
                }
                
                try
                {
                    var tc = builder.Build();
                    _logger.LogInformation("TOOL CALL: {Name}", tc.Name);
                    toolCalls.Add(tc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TOOL BUILDER [idx={Index}]: Failed to build tool call", idx);
                }
            }

            if (toolCalls.Count == 0)
            {
                _logger.LogWarning(
                    "Tool calls indicated but none were valid. BuilderCount={Count}, Content=\"{Content}\"",
                    toolCallBuilders.Count,
                    contentBuffer.ToString().Length > 100 
                        ? contentBuffer.ToString()[..100] + "..." 
                        : contentBuffer.ToString());
                conversation.AddAssistantMessage(contentBuffer.ToString());
                yield return AgentChunk.Complete();
                yield break;
            }

            // Record what the assistant said (including tool calls and reasoning details)
            var contentBeforeTools = contentBuffer.ToString();
            var reasoningToPreserve = reasoningDetails.Count > 0 ? reasoningDetails : null;
            conversation.AddAssistantToolCalls(toolCalls, reasoningToPreserve);

            _logger.LogInformation(
                "AGENT: Tool round {Round}: {Tools} (reasoning_details: {ReasoningCount})",
                toolRound,
                string.Join(", ", toolCalls.Select(t => t.Name)),
                reasoningDetails.Count);

            // Notify about tool execution
            foreach (var tc in toolCalls)
            {
                yield return AgentChunk.ToolExecuting(tc.Name);
            }

            // Execute tools
            var toolResults = await _toolExecutor.ExecuteAsync(toolCalls, parallel: true, ct);

            // Add results to conversation
            foreach (var result in toolResults)
            {
                conversation.AddToolResult(result.ToolCallId, result.Content);

                _logger.LogInformation(
                    "TOOL RESULT [{Tool}]: {Status} - {Content}",
                    result.ToolName,
                    result.Result.Status,
                    result.Content.Length > 150 
                        ? result.Content[..150] + "..." 
                        : result.Content);
            }

            _logger.LogInformation("AGENT: Tool results added, continuing to round {Next}", toolRound + 1);
            // Continue loop to get LLM response to tool results
        }

        _logger.LogWarning("AGENT: Exceeded max tool rounds ({Max})", MaxToolRounds);
        yield return AgentChunk.Complete();
    }

    /// <summary>
    /// Search memory for context relevant to the user's message.
    /// Returns formatted memory context or null if no relevant memories found.
    /// </summary>
    private async Task<string?> SearchMemoryAsync(string userMessage, CancellationToken ct)
    {
        if (_memory is not { IsInitialized: true })
        {
            _logger.LogDebug("AGENT: Memory not available, skipping search");
            return null;
        }
        
        try
        {
            _logger.LogInformation(
                "AGENT: Searching memory for: \"{Query}\"",
                userMessage.Length > 50 ? userMessage[..50] + "..." : userMessage);
            
            var searchOptions = new TimeDecaySearchOptions
            {
                OversampleCount = 30,
                RecencyWeight = 0.3f,
                HalfLifeHours = 72f,
                FinalCount = 5
            };
            
            var filter = new MemoryFilter
            {
                Types = ["exchange"]
            };
            
            var results = await _memory.SearchWithTimeDecayAsync(
                userMessage,
                searchOptions,
                filter,
                ct);
            
            if (results.Count == 0)
            {
                _logger.LogInformation("AGENT: No relevant memories found");
                return null;
            }
            
            _logger.LogInformation(
                "AGENT: Found {Count} relevant memories (top score: {Score:F3})",
                results.Count,
                results[0].Score);
            
            // Format memories for injection
            var sb = new StringBuilder();
            foreach (var result in results)
            {
                var dateStr = result.Memory.CreatedAt.ToString("MMM d");
                var content = result.Memory.Content;
                if (content.Length > 200)
                    content = content[..197] + "...";
                
                sb.AppendLine($"- {dateStr}: {content}");
            }
            
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AGENT: Memory search failed, continuing without context");
            return null;
        }
    }

    /// <summary>
    /// Try to extract a complete sentence from the buffer.
    /// Returns true if a sentence was found, with the sentence removed from the buffer.
    /// </summary>
    private static bool TryExtractSentence(StringBuilder buffer, out string sentence)
    {
        var text = buffer.ToString();

        // Match sentence-ending punctuation followed by whitespace or end
        // Handles: . ! ? and also ellipsis...
        var match = SentenceEndRegex().Match(text);

        if (match.Success)
        {
            sentence = match.Groups[1].Value.Trim();
            buffer.Remove(0, match.Length);
            return true;
        }

        sentence = "";
        return false;
    }

    // Matches a sentence ending with . ! ? followed by space or end of string
    // Group 1 captures the sentence including punctuation
    [GeneratedRegex(@"^(.+?[.!?]+)(?:\s+|$)", RegexOptions.Singleline)]
    private static partial Regex SentenceEndRegex();
}
