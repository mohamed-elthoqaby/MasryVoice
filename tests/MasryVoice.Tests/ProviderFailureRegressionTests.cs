using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MasryVoice.Api.Features.Knowledge;
using MasryVoice.Api.Features.Voice;
using Xunit;

namespace MasryVoice.Tests;

/// <summary>
/// Regression tests verifying that real providers do NOT silently fabricate transcripts,
/// synthetic tones, or fake embeddings when real services are offline or fail.
/// </summary>
public class ProviderFailureRegressionTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task WhisperSttProvider_ThrowsHttpRequestException_OnServerError_DoesNotReturnFabricatedTranscript()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Whisper CUDA out of memory")
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000") };
        var provider = new WhisperSttProvider(httpClient, NullLogger<WhisperSttProvider>.Instance);

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranscribeAudioAsync(audioStream, "audio/wav", "ar", CancellationToken.None));

        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task WhisperSttProvider_PreservesCancellation()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"text\": \"مرحبا\"}")
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000") };
        var provider = new WhisperSttProvider(httpClient, NullLogger<WhisperSttProvider>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TranscribeAudioAsync(audioStream, "audio/wav", "ar", cts.Token));
    }

    [Fact]
    public async Task LocalEgyptianTtsProvider_ThrowsHttpRequestException_OnServerError_DoesNotReturnSyntheticTone()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("TTS Engine unreachable")
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8001") };
        var provider = new LocalEgyptianTtsProvider(httpClient, NullLogger<LocalEgyptianTtsProvider>.Instance);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.SynthesizeSpeechAsync("أهلاً بحضرتك", "ar-EG", CancellationToken.None));

        Assert.Contains("502", ex.Message);
    }

    [Fact]
    public async Task LocalEgyptianTtsProvider_PreservesCancellation()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 82, 73, 70, 70 })
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8001") };
        var provider = new LocalEgyptianTtsProvider(httpClient, NullLogger<LocalEgyptianTtsProvider>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.SynthesizeSpeechAsync("أهلاً بحضرتك", "ar-EG", cts.Token));
    }

    [Fact]
    public async Task OllamaEmbeddingProvider_ThrowsHttpRequestException_OnServerError_DoesNotReturnHashVectors()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Ollama service down")
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Inference:EmbeddingModel"] = "all-minilm"
        }).Build();
        var provider = new OllamaEmbeddingProvider(httpClient, config, NullLogger<OllamaEmbeddingProvider>.Instance);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.GenerateEmbeddingAsync("استفسار عن حجز", CancellationToken.None));

        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task OllamaEmbeddingProvider_ThrowsInvalidOperationException_OnDimensionMismatch()
    {
        // 768 dimensions returned instead of expected 384
        var fakeIncompatibleVector = new float[768];
        var jsonResponse = JsonSerializer.Serialize(new { embedding = fakeIncompatibleVector });

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Inference:EmbeddingModel"] = "nomic-embed-text"
        }).Build();
        var provider = new OllamaEmbeddingProvider(httpClient, config, NullLogger<OllamaEmbeddingProvider>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GenerateEmbeddingAsync("استفسار عن حجز", CancellationToken.None));

        Assert.Contains("768 dimensions", ex.Message);
        Assert.Contains("384 dimensions", ex.Message);
    }

    [Fact]
    public async Task ExplicitSimulatedProviders_OperateInPredictableDemoMode()
    {
        var simStt = new SimulatedSttProvider("نص تجريبي");
        using var stream = new MemoryStream(new byte[] { 1 });
        var transcript = await simStt.TranscribeAudioAsync(stream);
        Assert.Equal("نص تجريبي", transcript);

        var simTts = new SimulatedTtsProvider();
        var audio = await simTts.SynthesizeSpeechAsync("مرحبا");
        Assert.True(audio.Length > 44); // Standard RIFF header is at least 44 bytes

        var detEmb = new DeterministicEmbeddingProvider();
        var vec = await detEmb.GenerateEmbeddingAsync("test");
        Assert.Equal(384, vec.Length);
    }
}
