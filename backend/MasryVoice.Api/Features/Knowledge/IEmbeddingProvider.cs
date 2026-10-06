using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Knowledge;

public interface IEmbeddingProvider
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Embedding provider using local Ollama (e.g. nomic-embed-text, all-minilm, or general model).
/// Falls back gracefully to deterministic embedding if Ollama embedding endpoint fails or model is missing.
/// </summary>
public class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly ILogger<OllamaEmbeddingProvider> _logger;
    private readonly DeterministicEmbeddingProvider _fallback;

    public OllamaEmbeddingProvider(HttpClient httpClient, IConfiguration configuration, ILogger<OllamaEmbeddingProvider> logger)
    {
        _httpClient = httpClient;
        _modelName = configuration["Inference:EmbeddingModel"] ?? "nomic-embed-text";
        _logger = logger;
        _fallback = new DeterministicEmbeddingProvider();
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new float[384];
        }

        try
        {
            var requestBody = new { model = _modelName, prompt = text };
            var response = await _httpClient.PostAsJsonAsync("/api/embeddings", requestBody, ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: ct);
                if (result?.Embedding != null && result.Embedding.Length > 0)
                {
                    // If Ollama returns e.g. 768 or other dimensions, project or truncate/pad to 384
                    return NormalizeDimension(result.Embedding, 384);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate embedding via Ollama '{Model}'. Using deterministic fallback.", _modelName);
        }

        return await _fallback.GenerateEmbeddingAsync(text, ct);
    }

    private static float[] NormalizeDimension(float[] source, int targetDim)
    {
        if (source.Length == targetDim) return source;

        var target = new float[targetDim];
        for (int i = 0; i < targetDim; i++)
        {
            target[i] = source[i % source.Length];
        }

        // L2 normalize
        float norm = 0f;
        for (int i = 0; i < targetDim; i++) norm += target[i] * target[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (int i = 0; i < targetDim; i++) target[i] /= norm;
        }

        return target;
    }

    private class OllamaEmbeddingResponse
    {
        public float[]? Embedding { get; set; }
    }
}

/// <summary>
/// Fast, deterministic 384-dimensional embedding provider for testing and offline execution.
/// </summary>
public class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var vector = new float[384];
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(vector);
        }

        var normalized = text.Trim().ToLowerInvariant();
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(word));
            for (int i = 0; i < hash.Length; i++)
            {
                int index = (i * 12) % 384;
                vector[index] += (hash[i] - 128f) / 128f;
            }
        }

        // L2 Normalization
        float sumSquares = 0f;
        for (int i = 0; i < 384; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        float length = MathF.Sqrt(sumSquares);
        if (length > 0)
        {
            for (int i = 0; i < 384; i++)
            {
                vector[i] /= length;
            }
        }

        return Task.FromResult(vector);
    }
}
