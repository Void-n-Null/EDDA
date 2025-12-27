using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EDDA.Server.Models;

namespace EDDA.Server.Services;

/// <summary>
/// HTTP client for the Chatterbox TTS microservice.
/// Implements circuit breaker and retry patterns for resilience.
/// </summary>
public class TtsService : ITtsService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TtsConfig _config;
    private readonly ILogger<TtsService> _logger;
    private readonly SemaphoreSlim _healthLock = new(1, 1);
    
    private Timer? _healthCheckTimer;
    private CircuitState _circuitState = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTime _circuitOpenedAt;
    
    // Circuit breaker states
    private enum CircuitState { Closed, Open, HalfOpen }
    
    public bool IsHealthy { get; private set; }
    public string? LastHealthStatus { get; private set; }
    
    public TtsService(TtsConfig config, ILogger<TtsService> logger)
    {
        _config = config;
        _logger = logger;
        
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
        };
        
        _logger.LogInformation("TTS Service configured: {Url}", config.BaseUrl);
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing TTS service health monitoring...");
        
        // Initial health check
        await CheckHealthAsync(cancellationToken);
        
        // Start periodic health checks
        _healthCheckTimer = new Timer(
            async _ => await CheckHealthAsync(CancellationToken.None),
            null,
            TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds),
            TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds));
        
        _logger.LogInformation(
            "TTS health monitoring started (interval: {Interval}s)",
            _config.HealthCheckIntervalSeconds);
    }
    
    public async Task<byte[]> GenerateSpeechAsync(
        string text,
        float exaggeration = 0.5f,
        CancellationToken cancellationToken = default)
    {
        return await GenerateSpeechWithVoiceAsync(text, null!, exaggeration, cancellationToken);
    }
    
    public async Task<byte[]> GenerateSpeechWithVoiceAsync(
        string text,
        string? voiceReferencePath,
        float exaggeration = 0.5f,
        CancellationToken cancellationToken = default)
    {
        // Check circuit breaker
        if (!CanMakeRequest())
        {
            throw new InvalidOperationException(
                $"TTS circuit breaker is open. Service unavailable until {_circuitOpenedAt.AddSeconds(_config.CircuitBreakerTimeoutSeconds):HH:mm:ss}");
        }
        
        var request = new TtsRequest
        {
            Text = text,
            VoiceReference = voiceReferencePath,
            Exaggeration = exaggeration,
            CfgWeight = _config.DefaultCfgWeight,
        };
        
        return await ExecuteWithRetryAsync(
            async ct => await SendTtsRequestAsync(request, ct),
            cancellationToken);
    }
    
    private async Task<byte[]> SendTtsRequestAsync(TtsRequest request, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/tts", request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"TTS request failed: {response.StatusCode} - {errorBody}");
            }
            
            var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var durationMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
            
            // Extract headers for logging
            var genTimeHeader = response.Headers.TryGetValues("X-Generation-Time-Ms", out var genValues)
                ? genValues.FirstOrDefault()
                : null;
            var rtfHeader = response.Headers.TryGetValues("X-Realtime-Factor", out var rtfValues)
                ? rtfValues.FirstOrDefault()
                : null;
            
            _logger.LogInformation(
                "TTS: {TextLen} chars -> {Bytes} bytes in {Duration:F0}ms (gen: {GenTime}ms, {Rtf}x RT)",
                request.Text.Length,
                audioBytes.Length,
                durationMs,
                genTimeHeader ?? "?",
                rtfHeader ?? "?");
            
            RecordSuccess();
            return audioBytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure();
            throw;
        }
    }
    
    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var lastException = default(Exception);
        var delay = _config.RetryDelayMs;
        
        for (var attempt = 1; attempt <= _config.RetryCount + 1; attempt++)
        {
            try
            {
                return await action(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                
                if (attempt <= _config.RetryCount)
                {
                    _logger.LogWarning(
                        ex,
                        "TTS request failed (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}ms",
                        attempt,
                        _config.RetryCount + 1,
                        delay);
                    
                    await Task.Delay(delay, cancellationToken);
                    delay *= 2; // Exponential backoff
                }
            }
        }
        
        _logger.LogError(lastException, "TTS request failed after {Attempts} attempts", _config.RetryCount + 1);
        throw lastException!;
    }
    
    private bool CanMakeRequest()
    {
        switch (_circuitState)
        {
            case CircuitState.Closed:
                return true;
                
            case CircuitState.Open:
                // Check if timeout has elapsed
                if (DateTime.UtcNow >= _circuitOpenedAt.AddSeconds(_config.CircuitBreakerTimeoutSeconds))
                {
                    _circuitState = CircuitState.HalfOpen;
                    _logger.LogInformation("TTS circuit breaker moving to half-open state");
                    return true;
                }
                return false;
                
            case CircuitState.HalfOpen:
                return true;
                
            default:
                return false;
        }
    }
    
    private void RecordSuccess()
    {
        _consecutiveFailures = 0;
        
        if (_circuitState == CircuitState.HalfOpen)
        {
            _circuitState = CircuitState.Closed;
            _logger.LogInformation("TTS circuit breaker closed (service recovered)");
        }
    }
    
    private void RecordFailure()
    {
        _consecutiveFailures++;
        
        if (_circuitState == CircuitState.HalfOpen)
        {
            // Failed during half-open, go back to open
            _circuitState = CircuitState.Open;
            _circuitOpenedAt = DateTime.UtcNow;
            _logger.LogWarning("TTS circuit breaker reopened (half-open test failed)");
        }
        else if (_consecutiveFailures >= _config.CircuitBreakerThreshold)
        {
            _circuitState = CircuitState.Open;
            _circuitOpenedAt = DateTime.UtcNow;
            _logger.LogWarning(
                "TTS circuit breaker opened after {Failures} consecutive failures",
                _consecutiveFailures);
        }
    }
    
    private async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!await _healthLock.WaitAsync(0, cancellationToken))
            return; // Skip if already checking
        
        try
        {
            var response = await _httpClient.GetAsync("/health", cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                var health = await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken);
                
                IsHealthy = health?.ModelLoaded == true;
                LastHealthStatus = health?.Status ?? "unknown";
                
                if (IsHealthy)
                {
                    _logger.LogDebug(
                        "TTS health OK - VRAM: {Used:F1}/{Total:F1} GB",
                        health?.VramUsedGb ?? 0,
                        health?.VramTotalGb ?? 0);
                }
                else
                {
                    _logger.LogWarning(
                        "TTS unhealthy: {Status} - {Error}",
                        health?.Status,
                        health?.LastError);
                }
            }
            else
            {
                IsHealthy = false;
                LastHealthStatus = $"HTTP {(int)response.StatusCode}";
                _logger.LogWarning("TTS health check failed: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            IsHealthy = false;
            LastHealthStatus = ex.Message;
            _logger.LogWarning(ex, "TTS health check exception");
        }
        finally
        {
            _healthLock.Release();
        }
    }
    
    public void Dispose()
    {
        _healthCheckTimer?.Dispose();
        _httpClient.Dispose();
        _healthLock.Dispose();
    }
    
    // ========================================================================
    // DTOs for JSON serialization
    // ========================================================================
    
    private class TtsRequest
    {
        [JsonPropertyName("text")]
        public string Text { get; init; } = "";
        
        [JsonPropertyName("voice_reference")]
        public string? VoiceReference { get; init; }
        
        [JsonPropertyName("exaggeration")]
        public float Exaggeration { get; init; }
        
        [JsonPropertyName("cfg_weight")]
        public float CfgWeight { get; init; }
    }
    
    private class HealthResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }
        
        [JsonPropertyName("model_loaded")]
        public bool ModelLoaded { get; init; }
        
        [JsonPropertyName("device")]
        public string? Device { get; init; }
        
        [JsonPropertyName("cuda_available")]
        public bool CudaAvailable { get; init; }
        
        [JsonPropertyName("vram_total_gb")]
        public float? VramTotalGb { get; init; }
        
        [JsonPropertyName("vram_used_gb")]
        public float? VramUsedGb { get; init; }
        
        [JsonPropertyName("last_error")]
        public string? LastError { get; init; }
    }
}

