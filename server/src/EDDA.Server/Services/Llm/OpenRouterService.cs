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
        var request = BuildRequest(messages, options, stream: true);
        var json = JsonSerializer.Serialize(request, JsonOptions);

        _logger?.LogDebug("OpenRouter stream request: {Request}", json);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data: "))
                continue;

            var data = line[6..];

            if (data == "[DONE]")
                yield break;

            StreamChunk? chunk;
            try
            {
                chunk = ParseStreamChunk(data);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Failed to parse stream chunk: {Data}", data);
                continue;
            }

            if (chunk is not null)
                yield return chunk;
        }
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
            return new
            {
                role = message.Role,
                content = message.Content,
                tool_calls = message.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new
                    {
                        name = tc.Name,
                        arguments = tc.Arguments.GetRawText()
                    }
                })
            };
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
                toolCalls.Add(new ToolCall
                {
                    Id = tc.GetProperty("id").GetString()!,
                    Name = function.GetProperty("name").GetString()!,
                    Arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!).RootElement.Clone()
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

    private static StreamChunk? ParseStreamChunk(string json)
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

        if (choice.TryGetProperty("delta", out var delta))
        {
            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind != JsonValueKind.Null)
            {
                contentDelta = contentProp.GetString();
            }

            if (delta.TryGetProperty("tool_calls", out var toolCallsProp) && toolCallsProp.ValueKind == JsonValueKind.Array)
            {
                toolCallDeltas = new List<ToolCallDelta>();
                foreach (var tc in toolCallsProp.EnumerateArray())
                {
                    var tcd = new ToolCallDelta
                    {
                        Index = tc.GetProperty("index").GetInt32()
                    };

                    if (tc.TryGetProperty("id", out var idProp))
                        tcd = tcd with { Id = idProp.GetString() };

                    if (tc.TryGetProperty("function", out var funcProp))
                    {
                        if (funcProp.TryGetProperty("name", out var nameProp))
                            tcd = tcd with { Name = nameProp.GetString() };

                        if (funcProp.TryGetProperty("arguments", out var argsProp))
                            tcd = tcd with { ArgumentsDelta = argsProp.GetString() };
                    }

                    toolCallDeltas.Add(tcd);
                }
            }
        }

        return new StreamChunk
        {
            ContentDelta = contentDelta,
            ToolCallDeltas = toolCallDeltas,
            FinishReason = finishReason
        };
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
