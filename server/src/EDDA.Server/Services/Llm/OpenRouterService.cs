using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EDDA.Server.Models;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Services.Llm;

/// <summary>
/// OpenRouter LLM service implementation.
/// </summary>
public class OpenRouterService : IOpenRouterService, IDisposable
{
    private readonly OpenRouterConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<OpenRouterService>? _logger;
    private readonly ToolExecutor? _toolExecutor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public bool IsInitialized { get; private set; }

    public OpenRouterService(
        OpenRouterConfig config,
        ILogger<OpenRouterService>? logger = null,
        ToolExecutor? toolExecutor = null)
    {
        _config = config;
        _logger = logger;
        _toolExecutor = toolExecutor;

        _http = new HttpClient
        {
            BaseAddress = new Uri(_config.BaseUrl),
            Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
        };

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        if (!string.IsNullOrWhiteSpace(_config.HttpReferer))
        {
            _http.DefaultRequestHeaders.Add("HTTP-Referer", _config.HttpReferer);
        }

        if (!string.IsNullOrWhiteSpace(_config.AppTitle))
        {
            _http.DefaultRequestHeaders.Add("X-Title", _config.AppTitle);
        }
    }

    public async Task InitializeAsync()
    {
        // Validate API key with a minimal request
        _logger?.LogInformation("Initializing OpenRouter service...");

        try
        {
            // Just verify we can reach the API
            var request = new HttpRequestMessage(HttpMethod.Get, "models");
            var response = await _http.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger?.LogInformation("OpenRouter service initialized (model: {Model})", _config.DefaultModel);
                IsInitialized = true;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger?.LogError("OpenRouter initialization failed: {Status} - {Body}",
                    response.StatusCode, body);
                IsInitialized = false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OpenRouter initialization failed");
            IsInitialized = false;
        }
    }

    public async Task<ChatResult> ChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var request = BuildRequest(messages, options, stream: false);
        var json = JsonSerializer.Serialize(request, JsonOptions);

        _logger?.LogDebug("OpenRouter request: {Request}", json);

        var response = await SendWithRetryAsync(json, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        _logger?.LogDebug("OpenRouter response: {Response}", responseJson);

        return ParseChatResponse(responseJson);
    }

    public async IAsyncEnumerable<StreamChunk> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messageList = messages.ToList();

        // Log if we're sending back tool calls with reasoning_details
        var toolCallMsgs = messageList.Where(m => m.ToolCalls is { Count: > 0 }).ToList();
        if (toolCallMsgs.Count > 0)
        {
            var reasoningCount = toolCallMsgs.Sum(m => m.ReasoningDetails?.Count ?? 0);
            _logger?.LogInformation(
                "LLM: Request includes {ToolMsgs} assistant message(s) with tool calls, {ReasoningCount} reasoning_details preserved",
                toolCallMsgs.Count, reasoningCount);
        }

        var request = BuildRequest(messageList, options, stream: true);
        var json = JsonSerializer.Serialize(request, JsonOptions);

        _logger?.LogInformation("LLM: Sending stream request ({Len} chars)", json.Length);

        // Log the full request if it contains reasoning_details (for debugging)
        if (json.Contains("reasoning_details"))
        {
            _logger?.LogDebug("LLM REQUEST WITH REASONING_DETAILS: {Json}", json);
        }

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogError("LLM STREAM ERROR: {Status} - {Body}", response.StatusCode, errorBody);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var chunkCount = 0;

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data: "))
            {
                _logger?.LogWarning("LLM STREAM: Unexpected line: {Line}", line);
                continue;
            }

            var data = line[6..];

            if (data == "[DONE]")
            {
                _logger?.LogDebug("LLM STREAM: Complete after {Count} chunks", chunkCount);
                yield break;
            }

            StreamChunk? chunk;
            try
            {
                chunk = ParseStreamChunk(data, _logger);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Failed to parse stream chunk: {Data}", data);
                continue;
            }

            if (chunk is not null)
            {
                chunkCount++;
                yield return chunk;
            }
        }

        _logger?.LogWarning("LLM STREAM: Reader ended without [DONE] after {Count} chunks", chunkCount);
    }

    public async Task<string> CompleteAsync(
        string prompt,
        string? systemPrompt = null,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(ChatMessage.System(systemPrompt));
        }

        messages.Add(ChatMessage.User(prompt));

        var result = await ChatAsync(messages, options, ct);

        return result.Content ?? "";
    }

    public async Task<ChatResult> ChatWithToolsAsync(
        IEnumerable<ChatMessage> messages,
        ToolDiscovery tools,
        ChatOptions? options = null,
        int maxToolRounds = 10,
        CancellationToken ct = default)
    {
        if (_toolExecutor is null)
        {
            throw new InvalidOperationException(
                "ToolExecutor not configured. Pass ToolExecutor to constructor to use ChatWithToolsAsync.");
        }

        var conversation = messages.ToList();
        var toolDefs = tools.GetOpenAiToolDefinitions().ToList();

        options = (options ?? new ChatOptions()) with { Tools = toolDefs };

        for (int round = 0; round < maxToolRounds; round++)
        {
            var result = await ChatAsync(conversation, options, ct);

            if (!result.HasToolCalls)
            {
                return result;
            }

            _logger?.LogInformation("Tool round {Round}: {Count} tool calls",
                round + 1, result.ToolCalls!.Count);

            // Add assistant message with tool calls
            conversation.Add(ChatMessage.AssistantWithToolCalls(result.ToolCalls!));

            // Execute tools and add results
            var toolResults = await _toolExecutor.ExecuteAsync(result.ToolCalls!, parallel: true, ct);

            foreach (var toolResult in toolResults)
            {
                conversation.Add(ChatMessage.Tool(toolResult.ToolCallId, toolResult.Content));
            }
        }

        _logger?.LogWarning("Exceeded max tool rounds ({Max}), returning last result", maxToolRounds);

        // Final call without tools to get a response
        options = options with { Tools = null };
        return await ChatAsync(conversation, options, ct);
    }

    private object BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var messageList = messages.Select(m => BuildMessageObject(m)).ToList();

        return new
        {
            model = options?.Model ?? _config.DefaultModel,
            messages = messageList,
            max_tokens = options?.MaxTokens ?? _config.MaxTokens,
            temperature = options?.Temperature ?? _config.Temperature,
            stream = options?.Stream ?? stream,
            tools = options?.Tools,
            tool_choice = options?.ToolChoice
        };
    }

    private static object BuildMessageObject(ChatMessage message)
    {
        if (message.ToolCalls is { Count: > 0 })
        {
            // Build tool calls
            var toolCallObjects = message.ToolCalls.Select(tc => new
            {
                id = tc.Id,
                type = "function",
                function = new
                {
                    name = tc.Name,
                    arguments = tc.Arguments.GetRawText()
                }
            }).ToList();

            // Build reasoning_details if present (required for Gemini 3 / Claude / OpenAI reasoning models)
            object? reasoningDetailsObj = null;
            if (message.ReasoningDetails is { Count: > 0 })
            {
                reasoningDetailsObj = message.ReasoningDetails.Select(rd =>
                {
                    var obj = new Dictionary<string, object?>
                    {
                        ["type"] = rd.Type
                    };
                    if (rd.Id != null) obj["id"] = rd.Id;
                    if (rd.Format != null) obj["format"] = rd.Format;
                    if (rd.Index != null) obj["index"] = rd.Index;
                    if (rd.Summary != null) obj["summary"] = rd.Summary;
                    if (rd.Text != null) obj["text"] = rd.Text;
                    if (rd.Signature != null) obj["signature"] = rd.Signature;
                    if (rd.Data != null) obj["data"] = rd.Data;
                    return obj;
                }).ToList();
            }

            // Use dictionary to conditionally include reasoning_details
            var msgObj = new Dictionary<string, object?>
            {
                ["role"] = message.Role,
                ["content"] = message.Content,
                ["tool_calls"] = toolCallObjects
            };

            if (reasoningDetailsObj != null)
            {
                msgObj["reasoning_details"] = reasoningDetailsObj;
            }

            return msgObj;
        }

        if (message.ToolCallId is not null)
        {
            return new
            {
                role = message.Role,
                tool_call_id = message.ToolCallId,
                content = message.Content
            };
        }

        return new
        {
            role = message.Role,
            content = message.Content
        };
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string json, CancellationToken ct)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        Exception? lastException = null;

        for (int attempt = 0; attempt <= _config.RetryCount; attempt++)
        {
            if (attempt > 0)
            {
                var delay = _config.RetryDelayMs * Math.Pow(2, attempt - 1);
                _logger?.LogDebug("Retry {Attempt} after {Delay}ms", attempt, delay);
                await Task.Delay((int)delay, ct);
            }

            try
            {
                var response = await _http.PostAsync("chat/completions", content, ct);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var body = await response.Content.ReadAsStringAsync(ct);

                // Don't retry client errors (except rate limiting)
                if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    throw new HttpRequestException($"OpenRouter API error: {response.StatusCode} - {body}");
                }

                _logger?.LogWarning("OpenRouter request failed: {Status} - {Body}",
                    response.StatusCode, body);

                lastException = new HttpRequestException($"OpenRouter API error: {response.StatusCode}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogWarning("OpenRouter request timed out");
                lastException = new TimeoutException("OpenRouter request timed out");
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogWarning(ex, "OpenRouter request failed");
                lastException = ex;
                throw; // Don't retry client errors
            }
        }

        throw lastException ?? new HttpRequestException("OpenRouter request failed after retries");
    }

    private static ChatResult ParseChatResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        string? content = null;
        if (message.TryGetProperty("content", out var contentProp) && contentProp.ValueKind != JsonValueKind.Null)
        {
            content = contentProp.GetString();
        }

        List<ToolCall>? toolCalls = null;
        if (message.TryGetProperty("tool_calls", out var toolCallsProp) && toolCallsProp.ValueKind == JsonValueKind.Array)
        {
            toolCalls = new List<ToolCall>();
            foreach (var tc in toolCallsProp.EnumerateArray())
            {
                var function = tc.GetProperty("function");

                // Capture thought_signature for Gemini 3 models
                string? thoughtSignature = null;
                if (tc.TryGetProperty("thought_signature", out var thoughtSigProp))
                {
                    thoughtSignature = thoughtSigProp.GetString();
                }

                toolCalls.Add(new ToolCall
                {
                    Id = tc.GetProperty("id").GetString()!,
                    Name = function.GetProperty("name").GetString()!,
                    Arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!).RootElement.Clone(),
                    ThoughtSignature = thoughtSignature
                });
            }
        }

        string? finishReason = null;
        if (choice.TryGetProperty("finish_reason", out var finishProp) && finishProp.ValueKind != JsonValueKind.Null)
        {
            finishReason = finishProp.GetString();
        }

        TokenUsage? usage = null;
        if (root.TryGetProperty("usage", out var usageProp))
        {
            usage = new TokenUsage
            {
                PromptTokens = usageProp.GetProperty("prompt_tokens").GetInt32(),
                CompletionTokens = usageProp.GetProperty("completion_tokens").GetInt32(),
                TotalTokens = usageProp.GetProperty("total_tokens").GetInt32()
            };
        }

        string? model = null;
        if (root.TryGetProperty("model", out var modelProp))
        {
            model = modelProp.GetString();
        }

        return new ChatResult
        {
            Content = content,
            ToolCalls = toolCalls,
            FinishReason = finishReason,
            Model = model,
            Usage = usage
        };
    }

    private static StreamChunk? ParseStreamChunk(string json, ILogger<OpenRouterService>? logger = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return null;

        var choice = choices[0];

        string? finishReason = null;
        if (choice.TryGetProperty("finish_reason", out var finishProp) && finishProp.ValueKind != JsonValueKind.Null)
        {
            finishReason = finishProp.GetString();
        }

        string? contentDelta = null;
        List<ToolCallDelta>? toolCallDeltas = null;
        List<ReasoningDetail>? reasoningDetails = null;

        if (choice.TryGetProperty("delta", out var delta))
        {
            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind != JsonValueKind.Null)
            {
                contentDelta = contentProp.GetString();
            }

            // Capture reasoning_details (required for Gemini 3 / Claude / OpenAI reasoning models)
            if (delta.TryGetProperty("reasoning_details", out var reasoningProp) && reasoningProp.ValueKind == JsonValueKind.Array)
            {
                logger?.LogInformation("RAW REASONING_DETAILS: {Json}", reasoningProp.GetRawText());
                reasoningDetails = ParseReasoningDetails(reasoningProp, logger);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCallsProp) && toolCallsProp.ValueKind == JsonValueKind.Array)
            {
                toolCallDeltas = new List<ToolCallDelta>();
                foreach (var tc in toolCallsProp.EnumerateArray())
                {
                    // Some models may not have 'index' - default to 0
                    var index = 0;
                    if (tc.TryGetProperty("index", out var indexProp))
                    {
                        index = indexProp.GetInt32();
                    }

                    var tcd = new ToolCallDelta { Index = index };

                    if (tc.TryGetProperty("id", out var idProp))
                        tcd = tcd with { Id = idProp.GetString() };

                    if (tc.TryGetProperty("function", out var funcProp))
                    {
                        if (funcProp.TryGetProperty("name", out var nameProp))
                            tcd = tcd with { Name = nameProp.GetString() };

                        if (funcProp.TryGetProperty("arguments", out var argsProp))
                            tcd = tcd with { ArgumentsDelta = argsProp.GetString() };
                    }

                    // Capture thought_signature if present (legacy/fallback)
                    if (tc.TryGetProperty("thought_signature", out var thoughtSigProp))
                    {
                        tcd = tcd with { ThoughtSignature = thoughtSigProp.GetString() };
                    }

                    logger?.LogDebug(
                        "PARSED TOOL DELTA: idx={Index}, id={Id}, name={Name}",
                        tcd.Index, tcd.Id, tcd.Name);

                    toolCallDeltas.Add(tcd);
                }
            }
        }

        return new StreamChunk
        {
            ContentDelta = contentDelta,
            ToolCallDeltas = toolCallDeltas,
            ReasoningDetails = reasoningDetails,
            FinishReason = finishReason
        };
    }

    private static List<ReasoningDetail>? ParseReasoningDetails(JsonElement reasoningProp, ILogger<OpenRouterService>? logger)
    {
        var details = new List<ReasoningDetail>();

        foreach (var item in reasoningProp.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeProp)
                ? typeProp.GetString() ?? "unknown"
                : "unknown";

            var detail = new ReasoningDetail
            {
                Type = type,
                Id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null,
                Format = item.TryGetProperty("format", out var formatProp) ? formatProp.GetString() : null,
                Index = item.TryGetProperty("index", out var indexProp) ? indexProp.GetInt32() : null,
                Summary = item.TryGetProperty("summary", out var summaryProp) ? summaryProp.GetString() : null,
                Text = item.TryGetProperty("text", out var textProp) ? textProp.GetString() : null,
                Signature = item.TryGetProperty("signature", out var sigProp) ? sigProp.GetString() : null,
                Data = item.TryGetProperty("data", out var dataProp) ? dataProp.GetString() : null
            };

            logger?.LogDebug(
                "PARSED REASONING: type={Type}, id={Id}, hasText={HasText}, hasSig={HasSig}",
                detail.Type, detail.Id, detail.Text != null, detail.Signature != null);

            details.Add(detail);
        }

        return details.Count > 0 ? details : null;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
