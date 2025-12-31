using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using EDDA.Server.Models;

namespace EDDA.Server.Services;

/// <summary>
/// HTTP client for TTS microservices (Chatterbox or Piper).
/// Implements circuit breaker, retry patterns, and runtime backend switching.
/// Handles voice file caching - uploads voice files to TTS service on demand.
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
    private readonly SemaphoreSlim _voiceUploadLock = new(1, 1);

    private Timer? _healthCheckTimer;
    private CircuitState _circuitState = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTime _circuitOpenedAt;
    private string? _lastLoggedEndpoint;

    // Voice cache: maps voice name -> (hash, bytes)
    private readonly Dictionary<string, (string Hash, byte[] Data)> _voiceCache = new();

    // Track which voice hashes have been confirmed uploaded to each endpoint
    private readonly HashSet<string> _uploadedVoiceHashes = new();

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
            Timeout = TimeSpan.FromMilliseconds(TtsConfig.EndpointHealthTimeoutMs)
        };

        // Initialize with first endpoint (will be updated on first request)
        _config.ActiveChatterboxEndpoint = config.ChatterboxEndpoints.FirstOrDefault();
        _httpClient = CreateHttpClient(config.ActiveUrl);

        // Log endpoint configuration at startup
        if (config.ChatterboxEndpoints.Count > 1)
        {
            var endpoints = string.Join(", ", config.ChatterboxEndpoints
                .OrderBy(e => e.Priority)
                .Select(e => e.Name));
            _logger.LogInformation("  Endpoints: {Endpoints}", endpoints);
        }

        // Load voice files from disk
        LoadVoiceFiles();
    }

    /// <summary>
    /// Load voice files from embedded resources and compute their hashes.
    /// Voice resources are expected at EDDA.Server.Resources.Voices.{name}.wav
    /// </summary>
    private void LoadVoiceFiles()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourcePrefix = "EDDA.Server.Resources.Voices.";

        var voiceResources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(resourcePrefix) && n.EndsWith(".wav"))
            .ToList();

        foreach (var resourceName in voiceResources)
        {
            try
            {
                // Extract voice name from resource name (e.g., "EDDA.Server.Resources.Voices.blondie.wav" -> "blondie")
                var voiceName = resourceName[resourcePrefix.Length..^4]; // Remove prefix and ".wav"

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    _logger.LogWarning("Failed to open embedded resource: {Name}", resourceName);
                    continue;
                }

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                var hash = ComputeVoiceHash(bytes);

                _voiceCache[voiceName] = (hash, bytes);
                _logger.LogInformation("Loaded embedded voice '{Name}' ({Size}KB, hash: {Hash})",
                    voiceName, bytes.Length / 1024, hash);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load embedded voice: {Name}", resourceName);
            }
        }

        if (_voiceCache.Count == 0)
        {
            _logger.LogWarning("No embedded voice resources found");
        }
        else
        {
            _logger.LogInformation("Loaded {Count} embedded voice(s): {Names}",
                _voiceCache.Count, string.Join(", ", _voiceCache.Keys));
        }
    }

    /// <summary>
    /// Compute SHA256 hash of voice data (first 16 hex chars).
    /// </summary>
    private static string ComputeVoiceHash(byte[] data)
    {
        var hashBytes = SHA256.HashData(data);
        return Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
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

            // Clear uploaded voice tracking (new endpoint might not have them)
            _uploadedVoiceHashes.Clear();

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
        // Initial health check
        await CheckHealthAsync(cancellationToken);

        // Start periodic health checks (silent - only logs on state changes)
        _healthCheckTimer = new Timer(
            async void (_) =>
            {
                try
                {
                    await CheckHealthAsync(CancellationToken.None);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "TTS health monitor crashed");
                }
            },
            null,
            TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds),
            TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds));
    }

    public async Task<byte[]> GenerateSpeechAsync(
        string text,
        float exaggeration = 0.5f,
        CancellationToken cancellationToken = default)
    {
        // Use configured voice (null = default voice)
        return await GenerateSpeechWithVoiceAsync(text, _config.VoiceName, exaggeration, cancellationToken);
    }

    public async Task<byte[]> GenerateSpeechWithVoiceAsync(
        string text,
        string? voiceName,
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

        // Resolve voice to hash (and ensure it's uploaded)
        string? voiceId = null;
        if (!string.IsNullOrWhiteSpace(voiceName) && _voiceCache.TryGetValue(voiceName, out var voiceData))
        {
            voiceId = voiceData.Hash;

            // Ensure voice is uploaded to TTS service
            await EnsureVoiceUploadedAsync(voiceData.Hash, voiceData.Data, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(voiceName))
        {
            _logger.LogWarning("Voice '{Name}' not found in cache, using default voice", voiceName);
        }

        var request = new TtsRequest
        {
            Text = text,
            VoiceId = voiceId,
            Exaggeration = exaggeration,
            CfgWeight = _config.DefaultCfgWeight,
        };

        return await ExecuteWithRetryAsync(
            async ct => await SendTtsRequestAsync(request, ct),
            cancellationToken);
    }

    /// <summary>
    /// Ensure a voice file is uploaded to the TTS service cache.
    /// </summary>
    private async Task EnsureVoiceUploadedAsync(string hash, byte[] data, CancellationToken cancellationToken)
    {
        // Quick check without lock - if already uploaded, skip
        if (_uploadedVoiceHashes.Contains(hash))
            return;

        await _voiceUploadLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_uploadedVoiceHashes.Contains(hash))
                return;

            // Check if TTS service already has this voice cached
            try
            {
                var checkResponse = await _httpClient.GetAsync($"/voice/{hash}", cancellationToken);
                if (checkResponse.IsSuccessStatusCode)
                {
                    var status = await checkResponse.Content.ReadFromJsonAsync<VoiceCacheStatus>(cancellationToken);
                    if (status?.Cached == true)
                    {
                        _logger.LogDebug("Voice {Hash} already cached on TTS service", hash);
                        _uploadedVoiceHashes.Add(hash);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check voice cache status, will try uploading");
            }

            // Upload the voice
            _logger.LogInformation("Uploading voice {Hash} ({Size}KB) to TTS service...", hash, data.Length / 1024);

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(data);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", $"{hash}.wav");

            var uploadResponse = await _httpClient.PostAsync($"/voice/{hash}", content, cancellationToken);

            if (uploadResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation("✓ Voice {Hash} uploaded successfully", hash);
                _uploadedVoiceHashes.Add(hash);
            }
            else
            {
                var error = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to upload voice {Hash}: {Status} - {Error}",
                    hash, uploadResponse.StatusCode, error);
            }
        }
        finally
        {
            _voiceUploadLock.Release();
        }
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

                // Clear uploaded voice tracking (new endpoint might not have them)
                _uploadedVoiceHashes.Clear();

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
    /// Checks both HTTP status AND model_loaded field to ensure endpoint is actually usable.
    /// </summary>
    private async Task<bool> IsEndpointHealthyAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TtsConfig.EndpointHealthTimeoutMs);

            var response = await _healthCheckClient.GetAsync($"{url}/health", cts.Token);
            if (!response.IsSuccessStatusCode)
                return false;

            // Must also check model_loaded - endpoint returning 200 with model_loaded=false is NOT healthy
            var health = await response.Content.ReadFromJsonAsync<HealthResponse>(cts.Token);
            return health?.ModelLoaded == true;
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

                // If we get a 404 for "voice not found", invalidate our upload cache
                // This handles the case where TTS service restarted and lost its voice cache
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                    && !string.IsNullOrEmpty(request.VoiceId)
                    && errorBody.Contains("not found in cache"))
                {
                    _logger.LogWarning("TTS voice cache miss for {VoiceId} - will re-upload on next attempt", request.VoiceId);
                    _uploadedVoiceHashes.Remove(request.VoiceId);
                }

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

            _logger.LogDebug(
                "TTS: {TextLen} chars -> {Bytes}B in {Duration:F0}ms ({Rtf}x RT)",
                request.Text.Length, audioBytes.Length, durationMs, rtfHeader ?? "?");

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

                // Only log state changes
                if (!IsHealthy)
                {
                    _logger.LogWarning("TTS unhealthy: {Status}", health?.Status);
                }
            }
            else
            {
                IsHealthy = false;
                LastHealthStatus = $"HTTP {(int)response.StatusCode}";
                _logger.LogWarning("TTS health check failed: {Status}", response.StatusCode);
            }
        }
        catch (Exception)
        {
            var wasHealthy = IsHealthy;
            IsHealthy = false;
            LastHealthStatus = "Connection failed";

            // Only log on state change (was healthy, now isn't)
            if (wasHealthy)
            {
                _logger.LogWarning("TTS connection lost");
            }
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
        _voiceUploadLock.Dispose();
    }

    // ========================================================================
    // DTOs for JSON serialization
    // ========================================================================

    private class TtsRequest
    {
        [JsonPropertyName("text")]
        public string Text { get; init; } = "";

        [JsonPropertyName("voice_id")]
        public string? VoiceId { get; init; }

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

    private class VoiceCacheStatus
    {
        [JsonPropertyName("voice_id")]
        public string? VoiceId { get; init; }

        [JsonPropertyName("cached")]
        public bool Cached { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }
    }
}
