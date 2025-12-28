using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EDDA.Server.Models;

namespace EDDA.Server.Services;

/// <summary>
/// HTTP client for TTS microservices (Chatterbox or Piper).
/// Implements circuit breaker, retry patterns, and runtime backend switching.
/// </summary>
public class TtsService : ITtsService, IDisposable
{
    private HttpClient _httpClient;
    private readonly HttpClient _healthCheckClient;
    private readonly TtsConfig _config;
    private readonly ILogger<TtsService> _logger;
    private readonly SemaphoreSlim _healthLock = new(1, 1);
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly SemaphoreSlim _endpointSelectLock = new(1, 1);
    
    private Timer? _healthCheckTimer;
    private CircuitState _circuitState = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTime _circuitOpenedAt;
    private string? _lastLoggedEndpoint;
    
    // Circuit breaker states
    private enum CircuitState { Closed, Open, HalfOpen }
    
    public bool IsHealthy { get; private set; }
    public string? LastHealthStatus { get; private set; }
    public TtsBackend ActiveBackend => _config.ActiveBackend;
    public TtsConfig Config => _config;
    
    public TtsService(TtsConfig config, ILogger<TtsService> logger)
    {
        _config = config;
        _logger = logger;
        
        // Fast HTTP client for endpoint health checks
        _healthCheckClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(config.EndpointHealthTimeoutMs)
        };
        
        // Initialize with first endpoint (will be updated on first request)
        _config.ActiveChatterboxEndpoint = config.ChatterboxEndpoints.FirstOrDefault();
        _httpClient = CreateHttpClient(config.ActiveUrl);
        
        _logger.LogInformation("TTS Service configured: {Backend} with {Count} endpoints", 
            config.BackendName, config.ChatterboxEndpoints.Count);
        foreach (var ep in config.ChatterboxEndpoints.OrderBy(e => e.Priority))
        {
            _logger.LogInformation("  Priority {Priority}: {Name} @ {Url}", ep.Priority, ep.Name, ep.Url);
        }
    }
    
    private HttpClient CreateHttpClient(string baseUrl)
    {
        return new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds),
        };
    }
    
    /// <summary>
    /// Switch to a different TTS backend at runtime.
    /// </summary>
    public async Task SwitchBackendAsync(TtsBackend backend, CancellationToken cancellationToken = default)
    {
        if (backend == _config.ActiveBackend)
        {
            _logger.LogDebug("Already using {Backend}, no switch needed", backend);
            return;
        }
        
        await _switchLock.WaitAsync(cancellationToken);
        try
        {
            var oldBackend = _config.ActiveBackend;
            _config.ActiveBackend = backend;
            
            // Dispose old client and create new one
            var oldClient = _httpClient;
            _httpClient = CreateHttpClient(_config.ActiveUrl);
            oldClient.Dispose();
            
            // Reset circuit breaker state for new backend
            _circuitState = CircuitState.Closed;
            _consecutiveFailures = 0;
            
            _logger.LogInformation("🔄 TTS switched: {OldBackend} → {NewBackend} @ {Url}",
                oldBackend, backend, _config.ActiveUrl);
            
            // Check health of new backend
            await CheckHealthAsync(cancellationToken);
        }
        finally
        {
            _switchLock.Release();
        }
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
        // Use configured voice reference (null = default "lucy" voice, path = voice cloning)
        return await GenerateSpeechWithVoiceAsync(text, _config.VoiceReference, exaggeration, cancellationToken);
    }
    
    public async Task<byte[]> GenerateSpeechWithVoiceAsync(
        string text,
        string? voiceReferencePath,
        float exaggeration = 0.5f,
        CancellationToken cancellationToken = default)
    {
        // Select best available endpoint before each request (for Chatterbox)
        if (_config.ActiveBackend == TtsBackend.Chatterbox)
        {
            await SelectBestEndpointAsync(cancellationToken);
        }
        
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
    
    /// <summary>
    /// Select the best available Chatterbox endpoint (lowest priority number that responds).
    /// Only logs when the active endpoint actually changes.
    /// </summary>
    private async Task SelectBestEndpointAsync(CancellationToken cancellationToken)
    {
        if (!await _endpointSelectLock.WaitAsync(0, cancellationToken))
            return; // Another selection in progress, skip
        
        try
        {
            ChatterboxEndpoint? bestEndpoint = null;
            
            // Check endpoints in priority order
            foreach (var endpoint in _config.ChatterboxEndpoints.OrderBy(e => e.Priority))
            {
                if (await IsEndpointHealthyAsync(endpoint.Url, cancellationToken))
                {
                    bestEndpoint = endpoint;
                    break; // Found the highest priority healthy endpoint
                }
            }
            
            // Fall back to first endpoint if none responded (let normal error handling deal with it)
            bestEndpoint ??= _config.ChatterboxEndpoints.FirstOrDefault();
            
            if (bestEndpoint == null)
                return;
            
            // Check if endpoint changed
            var currentEndpointUrl = _config.ActiveChatterboxEndpoint?.Url;
            if (currentEndpointUrl != bestEndpoint.Url)
            {
                var oldName = _config.ActiveChatterboxEndpoint?.Name ?? "None";
                _config.ActiveChatterboxEndpoint = bestEndpoint;
                
                // Recreate HTTP client for new endpoint
                var oldClient = _httpClient;
                _httpClient = CreateHttpClient(bestEndpoint.Url);
                oldClient.Dispose();
                
                // Reset circuit breaker for new endpoint
                _circuitState = CircuitState.Closed;
                _consecutiveFailures = 0;
                
                // Only log actual switches (not initial selection or same endpoint)
                if (_lastLoggedEndpoint != null && _lastLoggedEndpoint != bestEndpoint.Name)
                {
                    _logger.LogInformation("🔄 TTS endpoint switched: {Old} → {New} ({Url})",
                        oldName, bestEndpoint.Name, bestEndpoint.Url);
                }
                _lastLoggedEndpoint = bestEndpoint.Name;
            }
        }
        finally
        {
            _endpointSelectLock.Release();
        }
    }
    
    /// <summary>
    /// Quick health check for endpoint selection (fast timeout, no logging).
    /// </summary>
    private async Task<bool> IsEndpointHealthyAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_config.EndpointHealthTimeoutMs);
            
            var response = await _healthCheckClient.GetAsync($"{url}/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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
        _healthCheckClient.Dispose();
        _healthLock.Dispose();
        _switchLock.Dispose();
        _endpointSelectLock.Dispose();
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

