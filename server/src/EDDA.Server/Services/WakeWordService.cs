using EDDA.Server.Services.Llm;

namespace EDDA.Server.Services;

/// <summary>
/// LLM-based wake word detection service.
/// Uses a fast, cheap LLM to determine if a transcription sounds like the wake word.
/// </summary>
public class WakeWordService : IWakeWordService
{
    private readonly IOpenRouterService _llm;
    private readonly ILogger<WakeWordService> _logger;
    private readonly string _targetWakeWord;

    private const string WakeWordPrompt = """
        Your task is to determine if the user is trying to say the wake word.
        You will be given a transcription of the user's speech.
        You will need to consider the following:
        - Phonetic similarity (nixie, pixie, nicky, etc, neck see, next, etc)
        - Common mishearings from speech-to-text
        - Context clues suggesting address/invocation
        - The user is adressing someone by name (but the name has to be vaguely simmilar to the wake word)
        - The user is using a common mishearing of the wake word
        - The user is using a common phrase that is similar to the wake word
        - The user might be saying the wake word, but with a different accent or pronunciation

        Answer ONLY: YES or NO
        The user's transcripted speech: "{0}"
        """;

    public WakeWordService(
        IOpenRouterService llm,
        ILogger<WakeWordService> logger,
        string targetWakeWord = "nyxie")
    {
        _llm = llm;
        _logger = logger;
        _targetWakeWord = targetWakeWord;
    }

    public async Task<bool> IsWakeWordAsync(string transcription, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcription))
            return false;

        var prompt = string.Format(WakeWordPrompt, transcription, _targetWakeWord);

        try
        {
            var options = new ChatOptions
            {
                Model = "anthropic/claude-haiku-4.5",
                MaxTokens = 10,
                Temperature = 0.0f
            };

            var result = await _llm.CompleteAsync(prompt, options: options, ct: ct);
            
            _logger.LogInformation("Wake word LLM response: \"{Response}\" for input: \"{Input}\"",
                result.Trim(),
                transcription.Length > 50 ? transcription[..50] + "..." : transcription);
            
            var isWakeWord = result.Contains("YES", StringComparison.OrdinalIgnoreCase);

            return isWakeWord;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wake word check failed, assuming not wake word");
            return false;
        }
    }
}
