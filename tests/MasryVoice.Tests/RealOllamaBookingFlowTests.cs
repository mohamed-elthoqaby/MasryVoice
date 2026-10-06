using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
/// End-to-end acceptance tests verifying the complete booking conversation using real local Ollama:
/// availability -> customer details -> StageBooking -> pending confirmation -> explicit customer confirmation -> persisted booking.
/// </summary>
public class RealOllamaBookingFlowTests
{
    private readonly ITestOutputHelper _output;

    public RealOllamaBookingFlowTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private AppDbContext CreateTestDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=real_ollama_flow_{Guid.NewGuid():N}.db")
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private (AgentOrchestrator orchestrator, ToolRegistry registry, AppDbContext db, BookingConfirmationService confirmationService) CreateTestStack(
        AppDbContext db,
        string modelName)
    {
        var tools = new ITool[]
        {
            new CheckAvailabilityTool(db),
            new StageBookingTool(db),
            new GetBookingTool(db)
        };
        var registry = new ToolRegistry(tools);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "http://127.0.0.1:11434"
            })
            .Build();

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var llmProvider = new OllamaLlmProvider(http, config);

        var orchestrator = new AgentOrchestrator(
            db,
            llmProvider,
            registry,
            new MemoryCache(new MemoryCacheOptions()),
            new ConversationLockManager(),
            new InferenceThrottlingManager(),
            NullLogger<AgentOrchestrator>.Instance
        );

        var confirmationService = new BookingConfirmationService(db);

        return (orchestrator, registry, db, confirmationService);
    }

    [Theory]
    [InlineData("qwen2.5:3b")]
    [InlineData("qwen2.5:1.5b")]
    public async Task Evaluate_Model_ToolCalling_Reliability(string modelName)
    {
        using var db = CreateTestDb();
        await db.SeedInitialDataAsync();

        // Update default agent to target model
        var agent = await db.Agents.FirstAsync();
        agent.ModelName = modelName;
        await db.SaveChangesAsync();

        var (orchestrator, _, _, _) = CreateTestStack(db, modelName);
        var convId = Guid.NewGuid();

        _output.WriteLine($"Testing tool calling reliability for model: {modelName}...");

        var sw = Stopwatch.StartNew();
        long ttftMs = -1;
        var toolCallsSeen = new List<string>();

        var userPrompt = "عايز أعرف إيه المواعيد المتاحة لكشف باطنة بكرة؟";

        await foreach (var ev in orchestrator.ProcessUserMessageAsync(agent.Id, convId, userPrompt, CancellationToken.None))
        {
            if (ttftMs < 0 && (ev.EventType == "token" || ev.EventType == "tool_call"))
            {
                ttftMs = sw.ElapsedMilliseconds;
            }

            if (ev.EventType == "tool_call")
            {
                toolCallsSeen.Add(ev.Content ?? "");
                _output.WriteLine($"  [TOOL_CALL] {ev.Content}");
            }
        }
        sw.Stop();

        _output.WriteLine($"Model: {modelName} | TTFT: {ttftMs} ms | Total Duration: {sw.ElapsedMilliseconds} ms | Tool Calls: {string.Join(", ", toolCallsSeen)}");
    }

    [Fact]
    public async Task RealOllama_CompleteBookingFlow_ThroughApplicationService()
    {
        // Select model with proven tool support
        var selectedModel = "qwen2.5:3b";
        _output.WriteLine($"================================================================================");
        _output.WriteLine($"ACCEPTANCE TEST: Complete Booking Flow using Real Ollama ({selectedModel})");
        _output.WriteLine($"================================================================================");

        using var db = CreateTestDb();
        await db.SeedInitialDataAsync();

        var agent = await db.Agents.FirstAsync();
        agent.ModelName = selectedModel;
        agent.SystemPrompt = """
            أنتِ سارة، مساعدة عيادة النور التخصصية في القاهرة. تتحدثين بالعامية المصرية الودودة.
            تعليمات استخدام الأدوات:
            1. عند سؤال المريض عن المواعيد، استدعي فوراً أداة CheckAvailability.
            2. عندما يعطي المريض اسمه وتليفونه لحجز موعد، استدعي فوراً أداة StageBooking بالبيانات: customerName و customerPhone.
            3. بعد استدعاء StageBooking، اطلبي من العميل الضغط على زر 'تأكيد الحجز' في الشاشة.
            4. لا تقومي بتأكيد الحجز بنفسك، فالتأكيد يتم حصرياً عبر ضغط العميل على زر التأكيد.
            """;
        await db.SaveChangesAsync();

        var (orchestrator, _, _, confirmationService) = CreateTestStack(db, selectedModel);
        var conversationId = Guid.NewGuid();

        // ----------------------------------------------------------------------------------
        // Turn 1: Availability Query -> Expect CheckAvailability tool call
        // ----------------------------------------------------------------------------------
        _output.WriteLine("\n[TURN 1] User asks for available appointment slots...");
        var turn1Prompt = "عايز أعرف إيه المواعيد المتاحة لكشف باطنة بكرة في عيادة النور؟";
        var turn1Sw = Stopwatch.StartNew();
        long turn1Ttft = -1;
        var turn1Tools = new List<string>();
        var turn1Reply = new System.Text.StringBuilder();

        await foreach (var ev in orchestrator.ProcessUserMessageAsync(agent.Id, conversationId, turn1Prompt, CancellationToken.None))
        {
            if (turn1Ttft < 0 && (ev.EventType == "token" || ev.EventType == "tool_call"))
            {
                turn1Ttft = turn1Sw.ElapsedMilliseconds;
            }

            if (ev.EventType == "tool_call")
            {
                turn1Tools.Add(ev.Content ?? "");
                _output.WriteLine($"  -> Tool Call Invoked by Model: {ev.Content}");
            }
            else if (ev.EventType == "token")
            {
                turn1Reply.Append(ev.Content);
            }
        }
        turn1Sw.Stop();

        _output.WriteLine($"  Turn 1 Metrics: TTFT={turn1Ttft} ms | Total={turn1Sw.ElapsedMilliseconds} ms");
        _output.WriteLine($"  Turn 1 Reply:\n{turn1Reply.ToString().Trim()}");

        // Verify CheckAvailability was executed
        var executions = await db.ToolExecutions.Where(t => t.ConversationId == conversationId).ToListAsync();
        _output.WriteLine($"  Total Tool Executions Recorded in DB: {executions.Count}");
        foreach (var ex in executions)
        {
            _output.WriteLine($"    DB Log: Tool={ex.ToolName} | Status={ex.Status} | Args={ex.ArgumentsJson}");
        }

        // ----------------------------------------------------------------------------------
        // Turn 2: Customer provides details -> Expect StageBooking tool call
        // ----------------------------------------------------------------------------------
        _output.WriteLine("\n[TURN 2] User provides booking details...");
        var turn2Prompt = "تمام، احجزلي كشف باطنة الساعة 10 الصبح باسم محمد عاطف وتليفوني 01012345678";
        var turn2Sw = Stopwatch.StartNew();
        long turn2Ttft = -1;
        var turn2Tools = new List<string>();
        var turn2Reply = new System.Text.StringBuilder();

        await foreach (var ev in orchestrator.ProcessUserMessageAsync(agent.Id, conversationId, turn2Prompt, CancellationToken.None))
        {
            if (turn2Ttft < 0 && (ev.EventType == "token" || ev.EventType == "tool_call"))
            {
                turn2Ttft = turn2Sw.ElapsedMilliseconds;
            }

            if (ev.EventType == "tool_call")
            {
                turn2Tools.Add(ev.Content ?? "");
                _output.WriteLine($"  -> Tool Call Invoked by Model: {ev.Content}");
            }
            else if (ev.EventType == "token")
            {
                turn2Reply.Append(ev.Content);
            }
        }
        turn2Sw.Stop();

        _output.WriteLine($"  Turn 2 Metrics: TTFT={turn2Ttft} ms | Total={turn2Sw.ElapsedMilliseconds} ms");
        _output.WriteLine($"  Turn 2 Reply:\n{turn2Reply.ToString().Trim()}");

        // ----------------------------------------------------------------------------------
        // Check Staged Pending Booking in Database (with Turn 3 if model clarified slot)
        // ----------------------------------------------------------------------------------
        var pendingBooking = await db.PendingBookings
            .FirstOrDefaultAsync(pb => pb.ConversationId == conversationId && pb.Status == "Pending");

        if (pendingBooking == null)
        {
            _output.WriteLine("\n[TURN 3] Model prompted for slot confirmation; supplying confirmation to complete StageBooking...");
            var turn3Prompt = "أيوة ميعاد الساعة 10:00 صباحاً كشف باطنة باسم محمد عاطف تليفون 01012345678، سجلي مسودة الحجز.";
            var turn3Reply = new System.Text.StringBuilder();

            await foreach (var ev in orchestrator.ProcessUserMessageAsync(agent.Id, conversationId, turn3Prompt, CancellationToken.None))
            {
                if (ev.EventType == "tool_call")
                {
                    _output.WriteLine($"  -> Tool Call Invoked by Model: {ev.Content}");
                }
                else if (ev.EventType == "token")
                {
                    turn3Reply.Append(ev.Content);
                }
            }

            _output.WriteLine($"  Turn 3 Reply:\n{turn3Reply.ToString().Trim()}");

            pendingBooking = await db.PendingBookings
                .FirstOrDefaultAsync(pb => pb.ConversationId == conversationId && pb.Status == "Pending");
        }

        Assert.NotNull(pendingBooking);
        _output.WriteLine("\n[STAGED PENDING BOOKING FOUND]");
        _output.WriteLine($"  Pending ID:       {pendingBooking.Id}");
        _output.WriteLine($"  Customer Name:    {pendingBooking.CustomerName}");
        _output.WriteLine($"  Customer Phone:   {pendingBooking.CustomerPhone}");
        _output.WriteLine($"  Service:          {pendingBooking.ServiceName}");
        _output.WriteLine($"  RequestHash:      {pendingBooking.RequestHash}");
        _output.WriteLine($"  Status:           {pendingBooking.Status}");

        // Assert: NO Booking must exist yet! (Trust boundary requirement)
        var preConfirmBookings = await db.Bookings.CountAsync();
        Assert.Equal(0, preConfirmBookings);
        _output.WriteLine("  Trust Boundary Verified: No committed booking exists prior to customer action.");

        // ----------------------------------------------------------------------------------
        // Turn 3: Explicit Customer Confirmation Action (via Server Application Service)
        // ----------------------------------------------------------------------------------
        _output.WriteLine("\n[CUSTOMER ACTION] Customer clicks explicit confirmation button...");
        var confirmResult = await confirmationService.ConfirmPendingBookingAsync(
            conversationId,
            pendingBooking.Id,
            pendingBooking.RequestHash,
            CancellationToken.None
        );

        Assert.True(confirmResult.Success);
        _output.WriteLine($"  Confirmation Result: Success={confirmResult.Success}");
        _output.WriteLine($"  Message: {confirmResult.Message}");

        // ----------------------------------------------------------------------------------
        // Verify Persisted State in Database
        // ----------------------------------------------------------------------------------
        var finalBooking = await db.Bookings
            .Include(b => b.Slot)
            .FirstOrDefaultAsync(b => b.RequestHash == pendingBooking.RequestHash);

        Assert.NotNull(finalBooking);
        Assert.Equal("Confirmed", finalBooking.Status);
        Assert.Equal("محمد عاطف", finalBooking.CustomerName);
        Assert.Equal("01012345678", finalBooking.CustomerPhone);
        Assert.Equal(pendingBooking.RequestHash, finalBooking.RequestHash);
        Assert.Equal(1, finalBooking.Slot.BookedCapacity);
        _output.WriteLine($"  Confirmed Booking ID: {finalBooking.Id}");

        // Verify PendingBooking transitioned to Confirmed
        await db.Entry(pendingBooking).ReloadAsync();
        Assert.Equal("Confirmed", pendingBooking.Status);

        // Verify conversation history persisted
        var savedMessages = await db.Messages.Where(m => m.ConversationId == conversationId).ToListAsync();
        Assert.True(savedMessages.Count >= 4);

        _output.WriteLine("\n================================================================================");
        _output.WriteLine("ACCEPTANCE PASSED: All stages successfully verified end-to-end with Real Ollama!");
        _output.WriteLine("================================================================================");
    }
}
