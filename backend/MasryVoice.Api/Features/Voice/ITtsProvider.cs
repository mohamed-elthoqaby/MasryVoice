using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Voice;

public interface ITtsProvider
{
    Task<ReadOnlyMemory<byte>> SynthesizeSpeechAsync(string text, string languageCode = "ar-EG", CancellationToken ct = default);
}

/// <summary>
/// Production Egyptian Arabic Text-To-Speech Provider calling a real OpenAI-compatible TTS endpoint
/// (e.g. Piper, Kokoro, Edge-TTS bridge, or local neural voice server).
/// Strictly fails when the real endpoint is unavailable or returns errors.
/// Never fabricates synthetic sine tones on failure.
/// </summary>
public class LocalEgyptianTtsProvider : ITtsProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalEgyptianTtsProvider> _logger;

    public LocalEgyptianTtsProvider(HttpClient httpClient, ILogger<LocalEgyptianTtsProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ReadOnlyMemory<byte>> SynthesizeSpeechAsync(string text, string languageCode = "ar-EG", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text to synthesize cannot be null or empty.", nameof(text));
        }

        var body = new
        {
            input = text,
            voice = "ar-EG-SalmaNeural",
            response_format = "wav"
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/audio/speech", body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("TTS request failed with status code {StatusCode}: {Error}", (int)response.StatusCode, err);
            throw new HttpRequestException($"Local Egyptian TTS service returned HTTP {(int)response.StatusCode}: {err}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("TTS service returned an empty audio response.");
        }

        return bytes;
    }
}

/// <summary>
/// Explicitly selected Simulated/Demo TTS Provider for headless CI runs and offline test environments.
/// Generates compliant standard WAV audio containers (RIFF 16kHz 16-bit Mono) for browser Web Audio playback
/// without requiring a live TTS neural model server.
/// </summary>
public class SimulatedTtsProvider : ITtsProvider
{
    public Task<ReadOnlyMemory<byte>> SynthesizeSpeechAsync(string text, string languageCode = "ar-EG", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        return Task.FromResult(GenerateSyntheticWav(text.Length));
    }

    /// <summary>
    /// Generates valid PCM WAV audio container (16kHz, 16-bit, Mono) for testing
    /// </summary>
    public static ReadOnlyMemory<byte> GenerateSyntheticWav(int textLength)
    {
        int sampleRate = 16000;
        int durationMs = Math.Clamp(textLength * 45, 300, 4000);
        int numSamples = (sampleRate * durationMs) / 1000;
        int subChunk2Size = numSamples * 2;
        int chunkSize = 36 + subChunk2Size;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // RIFF header
        writer.Write("RIFF"u8);
        writer.Write(chunkSize);
        writer.Write("WAVE"u8);

        // fmt chunk
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);

        // data chunk
        writer.Write("data"u8);
        writer.Write(subChunk2Size);

        double freq = 440.0;
        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            short sample = (short)(Math.Sin(2 * Math.PI * freq * t) * 1500.0);
            writer.Write(sample);
        }

        return ms.ToArray();
    }
}
