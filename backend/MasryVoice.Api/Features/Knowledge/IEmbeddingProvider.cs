using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Knowledge;

public interface IEmbeddingProvider
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Production Embedding Provider using a local Ollama instance (e.g., all-minilm, bge-small, nomic-embed-text).
/// Strictly validates embedding dimensions against the 384-dimensional vector space.
/// Never silently pads, truncates, or substitutes SHA-256 fake hashes on failure.
/// </summary>
public class OllamaEmbeddingProvider : IEmbeddingProvider
{
    public const int ExpectedDimension = 384;
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly ILogger<OllamaEmbeddingProvider> _logger;

    public OllamaEmbeddingProvider(HttpClient httpClient, IConfiguration configuration, ILogger<OllamaEmbeddingProvider> logger)
    {
        _httpClient = httpClient;
        _modelName = configuration["Inference:EmbeddingModel"] ?? "all-minilm";
        _logger = logger;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text for embedding generation cannot be null or empty.", nameof(text));
        }

        var requestBody = new { model = _modelName, prompt = text };
        var response = await _httpClient.PostAsJsonAsync("/api/embeddings", requestBody, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Ollama embedding generation failed with status code {StatusCode}: {Error}", (int)response.StatusCode, err);
            throw new HttpRequestException($"Ollama embedding service returned HTTP {(int)response.StatusCode}: {err}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: ct);
        if (result?.Embedding == null || result.Embedding.Length == 0)
        {
            throw new InvalidOperationException($"Ollama embedding response for model '{_modelName}' was null or empty.");
        }

        // Strict dimension validation: Disallow silent padding/truncation that corrupts cosine similarity spaces
        if (result.Embedding.Length != ExpectedDimension)
        {
            throw new InvalidOperationException(
                $"Embedding model '{_modelName}' returned {result.Embedding.Length} dimensions, " +
                $"but the database vector space strictly requires {ExpectedDimension} dimensions. " +
                "Incompatible embedding dimensions must not be mixed, padded, or truncated.");
        }

        return result.Embedding;
    }

    private class OllamaEmbeddingResponse
    {
        public float[]? Embedding { get; set; }
    }
}

/// <summary>
/// Explicitly selected Deterministic Embedding Provider for offline unit/acceptance tests and local demos.
/// Produces consistent 384-dimensional unit-norm vectors without external model dependencies.
/// </summary>
public class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    public const int Dimension = 384;

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var vector = new float[Dimension];
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(vector);
        }

        // Generate deterministic seed using SHA-256 of text
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim().ToLowerInvariant()));
        var seed = BitConverter.ToInt32(hash, 0);
        var rng = new Random(seed);

        for (int i = 0; i < Dimension; i++)
        {
            vector[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        // L2 normalize
        float norm = 0f;
        for (int i = 0; i < Dimension; i++) norm += vector[i] * vector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (int i = 0; i < Dimension; i++) vector[i] /= norm;
        }

        return Task.FromResult(vector);
    }
}
