using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Voice;

public interface ISttProvider
{
    Task<string> TranscribeAudioAsync(Stream audioStream, string contentType = "audio/wav", string language = "ar", CancellationToken ct = default);
}

/// <summary>
/// Production Whisper STT Provider calling an OpenAI-compatible speech transcription endpoint
/// (e.g., faster-whisper, whisper.cpp, or local Whisper server).
/// Strictly fails when the real endpoint is unavailable or returns errors.
/// Never fabricates synthetic transcripts on failure.
/// </summary>
public class WhisperSttProvider : ISttProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WhisperSttProvider> _logger;

    public WhisperSttProvider(HttpClient httpClient, ILogger<WhisperSttProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> TranscribeAudioAsync(Stream audioStream, string contentType = "audio/wav", string language = "ar", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (audioStream == null || (audioStream.CanSeek && audioStream.Length == 0))
        {
            throw new ArgumentException("Audio stream cannot be null or empty.", nameof(audioStream));
        }

        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(audioStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(streamContent, "file", "audio.wav");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent(language), "language");

        var response = await _httpClient.PostAsync("/v1/audio/transcriptions", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Whisper STT request failed with status code {StatusCode}: {Error}", (int)response.StatusCode, err);
            throw new HttpRequestException($"Whisper STT service returned HTTP {(int)response.StatusCode}: {err}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("text", out var textProp))
        {
            var transcript = textProp.GetString();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                return transcript;
            }
        }

        throw new InvalidOperationException("Whisper STT response did not contain a valid 'text' transcription field.");
    }
}

/// <summary>
/// Explicitly selected Simulated/Demo STT Provider for headless CI runs and offline test environments.
/// Returns predictable Egyptian Arabic test transcriptions without external service dependencies.
/// </summary>
public class SimulatedSttProvider : ISttProvider
{
    private readonly string _cannedTranscript;

    public SimulatedSttProvider(string? cannedTranscript = null)
    {
        _cannedTranscript = cannedTranscript ?? "عايز أعرف المواعيد المتاحة بكرة للكشف يا سارة";
    }

    public Task<string> TranscribeAudioAsync(Stream audioStream, string contentType = "audio/wav", string language = "ar", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_cannedTranscript);
    }
}
