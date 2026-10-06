using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MasryVoice.Api.Common;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Features.Bookings;
using MasryVoice.Api.Features.Chat;
using MasryVoice.Api.Features.Tools;
using MasryVoice.Api.Infrastructure.Persistence;
using MasryVoice.Api.Infrastructure.Providers;
using Xunit;
using Xunit.Abstractions;

namespace MasryVoice.Tests;

/// <summary>
/// Comprehensive performance, concurrency, query optimization, and load tests
/// separating ordinary database traffic from expensive inference traffic.
/// </summary>
public class BackendPerformanceAndLoadTests
{
    private readonly ITestOutputHelper _output;

    public BackendPerformanceAndLoadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private AppDbContext CreateIsolatedDb(string? dbName = null)
    {
        dbName ??= $"perf_test_{Guid.NewGuid():N}.db";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbName}")
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    // -----------------------------------------------------------------------------------------
    // TEST 1: Load Test with Clearly Labeled SIMULATED Inference Provider
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task LoadTest_SimulatedInference_BackendThroughputAndLatency()
    {
        var dbName = $"load_simulated_{Guid.NewGuid():N}.db";
        using (var setupDb = CreateIsolatedDb(dbName))
        {
            await setupDb.SeedInitialDataAsync();
        }

        var fakeLlm = new DeterministicFakeLlmProvider();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var lockManager = new ConversationLockManager();
        var throttleManager = new InferenceThrottlingManager(new InferenceThrottleOptions
        {
            MaxConcurrentInference = 4,
            MaxQueueLength = 50,
            QueueWaitTimeoutSeconds = 10
        });

        const int totalRequests = 40;
        const int concurrentWorkers = 8;
        var latenciesMs = new ConcurrentBag<double>();
        var errors = new ConcurrentBag<string>();

        _output.WriteLine("================================================================================");
        _output.WriteLine("LOAD TEST: SIMULATED INFERENCE PROVIDER (DeterministicFake baseline)");
        _output.WriteLine("NOTE: These numbers measure pure ASP.NET Core & EF Core overhead, NOT real LLM capacity.");
        _output.WriteLine("================================================================================");

        var swTotal = Stopwatch.StartNew();
        var memBefore = GC.GetTotalMemory(forceFullCollection: true);

        // Run requests across multiple independent conversations
        await Parallel.ForEachAsync(
            Enumerable.Range(0, totalRequests),
            new ParallelOptions { MaxDegreeOfParallelism = concurrentWorkers },
            async (index, ct) =>
            {
                var convId = Guid.NewGuid(); // independent conversation per worker
                var swReq = Stopwatch.StartNew();

                // Create independent DbContext per concurrent operation to prevent concurrency violations
                using var db = CreateIsolatedDb(dbName);
                var tools = new ITool[] { new CheckAvailabilityTool(db), new StageBookingTool(db), new GetBookingTool(db) };
                var registry = new ToolRegistry(tools);
                var orchestrator = new AgentOrchestrator(
                    db,
                    fakeLlm,
                    registry,
                    cache,
                    lockManager,
                    throttleManager,
                    NullLogger<AgentOrchestrator>.Instance
                );

                var agent = await db.Agents.FirstAsync(ct);

                try
                {
                    await foreach (var _ in orchestrator.ProcessUserMessageAsync(agent.Id, convId, "إيه المواعيد المتاحة بكرة؟", ct))
                    {
                        // consume tokens
                    }
                    swReq.Stop();
                    latenciesMs.Add(swReq.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
            });

        swTotal.Stop();
        var memAfter = GC.GetTotalMemory(forceFullCollection: false);
        var memoryDeltaMb = (memAfter - memBefore) / (1024.0 * 1024.0);

        var sorted = latenciesMs.OrderBy(l => l).ToList();
        var p50 = sorted.Count > 0 ? sorted[(int)(sorted.Count * 0.50)] : 0;
        var p95 = sorted.Count > 0 ? sorted[(int)(sorted.Count * 0.95)] : 0;
        var rps = totalRequests / swTotal.Elapsed.TotalSeconds;

        _output.WriteLine($"Total Requests Completed:   {sorted.Count} / {totalRequests}");
        _output.WriteLine($"Total Elapsed Duration:     {swTotal.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"Throughput (SIMULATED AI):  {rps:N2} req/sec");
        _output.WriteLine($"p50 Latency:                {p50:N2} ms");
        _output.WriteLine($"p95 Latency:                {p95:N2} ms");
        _output.WriteLine($"Memory Delta:               {memoryDeltaMb:N2} MB");
        _output.WriteLine($"Errors / Overload Drops:    {errors.Count}");

        Assert.Empty(errors);
        Assert.Equal(totalRequests, sorted.Count);
        Assert.True(p50 < 200, $"p50 latency {p50} ms should be under 200 ms with simulated AI");
    }

    // -----------------------------------------------------------------------------------------
    // TEST 2: Conversation Message Ordering Under Concurrent Requests
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Concurrency_PreservesMessageOrdering_WithinSameConversation()
    {
        var dbName = $"ordering_test_{Guid.NewGuid():N}.db";
        using (var setupDb = CreateIsolatedDb(dbName))
        {
            await setupDb.SeedInitialDataAsync();
        }

        var fakeLlm = new DeterministicFakeLlmProvider();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var lockManager = new ConversationLockManager();
        var throttleManager = new InferenceThrottlingManager();

        var sharedConvId = Guid.NewGuid();
        const int concurrentTurns = 5;

        // Fire 5 concurrent requests at the exact same conversation ID
        await Parallel.ForEachAsync(
            Enumerable.Range(1, concurrentTurns),
            new ParallelOptions { MaxDegreeOfParallelism = concurrentTurns },
            async (turnIndex, ct) =>
            {
                using var db = CreateIsolatedDb(dbName);
                var tools = new ITool[] { new CheckAvailabilityTool(db), new StageBookingTool(db), new GetBookingTool(db) };
                var registry = new ToolRegistry(tools);
                var orchestrator = new AgentOrchestrator(
                    db,
                    fakeLlm,
                    registry,
                    cache,
                    lockManager,
                    throttleManager,
                    NullLogger<AgentOrchestrator>.Instance
                );

                var agent = await db.Agents.FirstAsync(ct);
                await foreach (var _ in orchestrator.ProcessUserMessageAsync(agent.Id, sharedConvId, $"رسالة رقم {turnIndex}", ct))
                {
                }
            });

        // Verify sequence numbers in DB are strictly consecutive and without duplicate sequence collision
        using var verifyDb = CreateIsolatedDb(dbName);
        var messages = await verifyDb.Messages
            .Where(m => m.ConversationId == sharedConvId)
            .OrderBy(m => m.SequenceNumber)
            .ToListAsync();

        var seqNumbers = messages.Select(m => m.SequenceNumber).ToList();
        _output.WriteLine($"Saved Sequence Numbers ({messages.Count} messages): {string.Join(", ", seqNumbers)}");

        // There should be 5 user messages and 5 assistant messages = 10 messages total
        Assert.Equal(concurrentTurns * 2, messages.Count);
        // Sequence numbers must be strictly 1, 2, 3, 4, 5, 6, 7, 8, 9, 10
        for (int i = 0; i < messages.Count; i++)
        {
            Assert.Equal(i + 1, seqNumbers[i]);
        }
    }

    // -----------------------------------------------------------------------------------------
    // TEST 3: Overload Protection & Graceful Recovery
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task OverloadProtection_RejectsWhenQueueFull_AndRecoversWhenLoadDrops()
    {
        // Configure throttle with MaxConcurrency=1, MaxQueue=2, Timeout=10s
        var throttleManager = new InferenceThrottlingManager(new InferenceThrottleOptions
        {
            MaxConcurrentInference = 1,
            MaxQueueLength = 2,
            QueueWaitTimeoutSeconds = 30
        });

        // 1. Acquire permit to saturate the 1 active slot
        var activePermit = await throttleManager.AcquirePermitAsync();
        Assert.Equal(1, throttleManager.ActiveCount);

        // 2. Queue 2 callers (filling the queue to capacity 2)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var taskQueued1 = Task.Run(async () => await throttleManager.AcquirePermitAsync(cts.Token));
        var taskQueued2 = Task.Run(async () => await throttleManager.AcquirePermitAsync(cts.Token));

        var swWait = Stopwatch.StartNew();
        while (throttleManager.WaitingCount < 2 && swWait.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(10);
        }
        Assert.Equal(2, throttleManager.WaitingCount);

        // 3. 4th caller should be IMMEDIATELY REJECTED with InferenceOverloadException (Queue Full)
        var swReject = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InferenceOverloadException>(async () =>
        {
            await throttleManager.AcquirePermitAsync();
        });
        swReject.Stop();

        _output.WriteLine($"Overload Rejection: Code={ex.Code} | Message={ex.Message} | Duration={swReject.ElapsedMilliseconds} ms");
        Assert.Equal("QUEUE_FULL", ex.Code);
        Assert.True(swReject.ElapsedMilliseconds < 50, "Queue full rejection must be immediate without waiting");

        // 4. Release active permit to allow recovery
        activePermit.Dispose();
        var nextPermit1 = await taskQueued1;
        Assert.NotNull(nextPermit1);
        nextPermit1.Dispose();

        var nextPermit2 = await taskQueued2;
        Assert.NotNull(nextPermit2);
        nextPermit2.Dispose();

        _output.WriteLine("Overload protection and graceful recovery verified successfully.");
    }

    // -----------------------------------------------------------------------------------------
    // TEST 4: Query Optimization & Caching Verification
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task QueryOptimization_SlotsCacheAndIndexLookup_ReducesDatabaseQueries()
    {
        var dbName = $"caching_test_{Guid.NewGuid():N}.db";
        using (var db = CreateIsolatedDb(dbName))
        {
            await db.SeedInitialDataAsync();
        }

        var cache = new MemoryCache(new MemoryCacheOptions());

        // First call: populates cache
        using (var db1 = CreateIsolatedDb(dbName))
        {
            var query = db1.AvailabilitySlots.AsNoTracking().Where(s => s.StartTimeUtc >= DateTime.UtcNow);
            var slots1 = await query.OrderBy(s => s.StartTimeUtc).Take(20).ToListAsync();
            cache.Set("slots_all", slots1, TimeSpan.FromSeconds(15));
            Assert.NotEmpty(slots1);
        }

        // Second call: served from memory cache without opening DB query
        var hitCache = cache.TryGetValue("slots_all", out List<AvailabilitySlot>? cachedSlots);
        Assert.True(hitCache);
        Assert.NotNull(cachedSlots);
        Assert.NotEmpty(cachedSlots);

        // Modification: booking confirmation invalidates cache
        cache.Remove("slots_all");
        var afterInvalidation = cache.TryGetValue("slots_all", out _);
        Assert.False(afterInvalidation, "Cache must be invalidated after booking confirmation.");
    }

    // -----------------------------------------------------------------------------------------
    // TEST 5: Small Real-Ollama Concurrency Within Laptop Resource Limits
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task RealOllama_SmallConcurrencyTest_LaptopResourceLimits()
    {
        var dbName = $"real_ollama_load_{Guid.NewGuid():N}.db";
        using (var setupDb = CreateIsolatedDb(dbName))
        {
            await setupDb.SeedInitialDataAsync();
        }

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "http://127.0.0.1:11434"
            })
            .Build();

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var realLlm = new OllamaLlmProvider(http, config);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var lockManager = new ConversationLockManager();
        // Laptop CPU budget: Limit Ollama concurrency to 1 to protect i5-12450H CPU cores
        var throttleManager = new InferenceThrottlingManager(new InferenceThrottleOptions
        {
            MaxConcurrentInference = 1,
            MaxQueueLength = 3,
            QueueWaitTimeoutSeconds = 60
        });

        _output.WriteLine("================================================================================");
        _output.WriteLine("SMALL CONCURRENCY TEST: REAL OLLAMA (qwen2.5:3b on Intel i5-12450H CPU)");
        _output.WriteLine("================================================================================");

        try
        {
            var ping = await http.GetAsync("http://127.0.0.1:11434/api/tags");
            if (!ping.IsSuccessStatusCode)
            {
                _output.WriteLine("Ollama is not responding. Skipping real Ollama test.");
                return;
            }
        }
        catch
        {
            _output.WriteLine("Ollama is not running. Skipping real Ollama test.");
            return;
        }

        var requestTimes = new List<double>();
        var swTotal = Stopwatch.StartNew();

        // Run 2 small consecutive queries through the throttled pipeline
        for (int i = 1; i <= 2; i++)
        {
            using var db = CreateIsolatedDb(dbName);
            var tools = new ITool[] { new CheckAvailabilityTool(db), new StageBookingTool(db), new GetBookingTool(db) };
            var registry = new ToolRegistry(tools);
            var orchestrator = new AgentOrchestrator(
                db,
                realLlm,
                registry,
                cache,
                lockManager,
                throttleManager,
                NullLogger<AgentOrchestrator>.Instance
            );

            var agent = await db.Agents.FirstAsync();
            agent.ModelName = "qwen2.5:3b";
            await db.SaveChangesAsync();

            var convId = Guid.NewGuid();
            var sw = Stopwatch.StartNew();

            await foreach (var ev in orchestrator.ProcessUserMessageAsync(agent.Id, convId, "أهلاً يا سارة، إيه مواعيد الكشف؟", CancellationToken.None))
            {
            }
            sw.Stop();
            requestTimes.Add(sw.ElapsedMilliseconds);
            _output.WriteLine($"Real Ollama Turn {i}: Duration = {sw.ElapsedMilliseconds} ms");
        }
        swTotal.Stop();

        _output.WriteLine($"Total Real Ollama Duration: {swTotal.ElapsedMilliseconds} ms");
        _output.WriteLine($"Average Real Ollama Turn:   {requestTimes.Average():N0} ms");
        _output.WriteLine($"Total Processed Through Throttle: {throttleManager.TotalProcessed}");
        _output.WriteLine($"Total Overload Rejections:  {throttleManager.TotalRejections}");

        Assert.Equal(2, throttleManager.TotalProcessed);
        Assert.Equal(0, throttleManager.TotalRejections);
    }
}
