using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Features.Chat;
using MasryVoice.Api.Features.Tools;
using MasryVoice.Api.Features.Voice;
using MasryVoice.Api.Infrastructure.Persistence;
using MasryVoice.Api.Infrastructure.Providers;
using Xunit;

namespace MasryVoice.Tests;

public class PhaseAOrchestratorAndVoiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(_handler(req));
    }

    private class CountingLlmProvider : ILlmProvider
    {
        public int CallCount { get; private set; }
        private readonly Func<int, LlmChatRequest, IEnumerable<LlmStreamChunk>> _chunkFactory;

        public CountingLlmProvider(Func<int, LlmChatRequest, IEnumerable<LlmStreamChunk>> chunkFactory)
        {
            _chunkFactory = chunkFactory;
        }

        public string ProviderName => "CountingMock";

        public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
            LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            int thisCall = CallCount;
            foreach (var chunk in _chunkFactory(thisCall, request))
            {
                yield return chunk;
            }
            await Task.CompletedTask;
        }
    }

    private class RejectOnSecondCallThrottle : InferenceThrottlingManager
    {
        private int _count;
        public override async Task<IDisposable> AcquirePermitAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _count) > 1)
            {
                throw new InferenceOverloadException("Overloaded on retry", code: "QUEUE_FULL", retryAfterSeconds: 5);
            }
            return await base.AcquirePermitAsync(ct);
        }
    }

    private AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=test_phaseA_{Guid.NewGuid():N}.db")
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Theory]
    [InlineData("audio/webm;codecs=opus", "audio/webm")]
    [InlineData("audio/ogg; codecs=opus", "audio/ogg")]
    [InlineData("audio/wav", "audio/wav")]
    [InlineData(null, "audio/wav")]
    [InlineData("invalid-content-type", "audio/wav")]
    public void NormalizeMediaType_HandlesAllBrowserShapes(string? input, string expected)
    {
        var result = WhisperSttProvider.NormalizeMediaType(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task WhisperSttProvider_AcceptsWebmOpus_WithoutThrowingFormatException()
    {
        HttpRequestMessage? captured = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\": \"كشف باطنة\"}")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000") };
        var provider = new WhisperSttProvider(client, NullLogger<WhisperSttProvider>.Instance);

        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var text = await provider.TranscribeAudioAsync(ms, "audio/webm;codecs=opus", "ar");

        Assert.Equal("كشف باطنة", text);
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task AgentOrchestrator_WhenRetryPermitRejected_EmitsOverloadEvent_AndMakesZeroRetryUpstreamCalls()
    {
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();
        var agent = await db.Agents.FirstAsync();
        var tools = new List<ITool> { new CheckAvailabilityTool(db) };
        var registry = new ToolRegistry(tools);

        var throttle = new RejectOnSecondCallThrottle();

        var countingProvider = new CountingLlmProvider((callNum, req) =>
        {
            if (callNum == 1)
            {
                return new[] { new LlmStreamChunk(IsCompleted: true, FinishReason: "stop") };
            }
            return new[] { new LlmStreamChunk(DeltaText: "should never run") };
        });

        var orchestrator = new AgentOrchestrator(
            db,
            countingProvider,
            registry,
            new MemoryCache(new MemoryCacheOptions()),
            new ConversationLockManager(),
            throttle,
            NullLogger<AgentOrchestrator>.Instance
        );

        var events = new List<ChatEvent>();
        await foreach (var ev in orchestrator.ProcessUserMessageAsync(agent.Id, Guid.NewGuid(), "صباح الخير", CancellationToken.None))
        {
            events.Add(ev);
        }

        // Exactly 1 upstream call occurred (the first pass). The retry was blocked by admission control.
        Assert.Equal(1, countingProvider.CallCount);

        var err = events.FirstOrDefault(e => e.EventType == "error");
        Assert.NotNull(err);
        var info = Assert.IsType<ChatErrorInfo>(err.Metadata);
        Assert.Equal("QUEUE_FULL", info.Code);
        Assert.Equal("llm", info.Stage);

        // No "done" event was emitted
        Assert.DoesNotContain(events, e => e.EventType == "done");
    }

    [Fact]
    public async Task AgentOrchestrator_WhenRetryFailsUpstream_EmitsErrorEvent_NeverDone()
    {
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();
        var agent = await db.Agents.FirstAsync();
        var tools = new List<ITool> { new CheckAvailabilityTool(db) };
        var registry = new ToolRegistry(tools);

        var countingProvider = new CountingLlmProvider((callNum, req) =>
        {
            if (callNum == 1)
            {
                // First pass: 0 tokens, 0 tools
                return new[] { new LlmStreamChunk(IsCompleted: true, FinishReason: "stop") };
            }
            // Second pass (retry): upstream failure
            return new[] { new LlmStreamChunk(IsError: true, ErrorMessage: "Ollama 500 server crash") };
        });

        var orchestrator = new AgentOrchestrator(
            db,
            countingProvider,
            registry,
            new MemoryCache(new MemoryCacheOptions()),
            new ConversationLockManager(),
            new InferenceThrottlingManager(),
            NullLogger<AgentOrchestrator>.Instance
        );

        var events = new List<ChatEvent>();
        await foreach (var ev in orchestrator.ProcessUserMessageAsync(agent.Id, Guid.NewGuid(), "أهلاً", CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Equal(2, countingProvider.CallCount);
        var err = events.FirstOrDefault(e => e.EventType == "error");
        Assert.NotNull(err);
        var info = Assert.IsType<ChatErrorInfo>(err.Metadata);
        Assert.Equal("LLM_PROVIDER_UNAVAILABLE", info.Code);

        // Never emit "done" on retry error
        Assert.DoesNotContain(events, e => e.EventType == "done");
    }

    [Fact]
    public void VoiceFailureClassifier_ClassifiesExpectedPipelineExceptions()
    {
        var overload = new VoiceOverloadException("Busy", "STT_QUEUE_FULL", 3);
        var f1 = VoiceFailureClassifier.Classify(overload, "stt");
        Assert.Equal(429, f1.StatusCode);
        Assert.Equal("STT_QUEUE_FULL", f1.Code);

        var argEx = new ArgumentException("empty", "audioStream");
        var f2 = VoiceFailureClassifier.Classify(argEx, "stt");
        Assert.Equal(400, f2.StatusCode);
        Assert.Equal("STT_EMPTY_AUDIO", f2.Code);

        var emptyTranscript = new InvalidOperationException("No text");
        var f3 = VoiceFailureClassifier.Classify(emptyTranscript, "stt");
        Assert.Equal(422, f3.StatusCode);
        Assert.Equal("STT_EMPTY_TRANSCRIPT", f3.Code);

        var http500 = new HttpRequestException("fail", null, HttpStatusCode.InternalServerError);
        var f4 = VoiceFailureClassifier.Classify(http500, "stt");
        Assert.Equal(502, f4.StatusCode);
        Assert.Equal("STT_PROVIDER_ERROR", f4.Code);

        var connRefused = new HttpRequestException("connection refused");
        var f5 = VoiceFailureClassifier.Classify(connRefused, "tts");
        Assert.Equal(503, f5.StatusCode);
        Assert.Equal("TTS_PROVIDER_UNAVAILABLE", f5.Code);
    }
}
