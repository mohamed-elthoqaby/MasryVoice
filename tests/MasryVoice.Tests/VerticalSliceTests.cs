using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MasryVoice.Api.Common;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Features.Bookings;
using MasryVoice.Api.Features.Chat;
using MasryVoice.Api.Features.Tools;
using MasryVoice.Api.Infrastructure.Persistence;
using MasryVoice.Api.Infrastructure.Providers;
using Xunit;

namespace MasryVoice.Tests;

public class VerticalSliceTests
{
    private AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=test_{Guid.NewGuid():N}.db")
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task CheckAvailabilityTool_Returns_AvailableCairoSlots()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();

        var firstSlot = await db.AvailabilitySlots.OrderBy(s => s.StartTimeUtc).FirstAsync();
        var targetBusinessDate = CairoTimeHelper.UtcToCairo(firstSlot.StartTimeUtc).ToString("yyyy-MM-dd");

        var tool = new CheckAvailabilityTool(db);
        using var argsDoc = JsonDocument.Parse($"{{\"date\":\"{targetBusinessDate}\",\"service\":\"كشف باطنة عامة\"}}");

        // Act
        var result = await tool.ExecuteAsync(argsDoc.RootElement, Guid.NewGuid(), CancellationToken.None);

        // Assert: Asserts actual available slots on valid business day
        Assert.True(result.Success);
        Assert.Contains("تم العثور على", result.Message);
        Assert.NotNull(result.Data);

        var dataJson = JsonSerializer.Serialize(result.Data);
        using var doc = JsonDocument.Parse(dataJson);
        var slots = doc.RootElement.GetProperty("availableSlots");
        Assert.True(slots.GetArrayLength() > 0);
        foreach (var slot in slots.EnumerateArray())
        {
            Assert.Equal("كشف باطنة عامة", slot.GetProperty("service").GetString());
            Assert.False(string.IsNullOrWhiteSpace(slot.GetProperty("cairoTime").GetString()));
        }
    }

    [Fact]
    public async Task CheckAvailabilityTool_OnClosedDay_ReturnsNoSlotsForDay_AndSuggestsNextAvailable()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();

        // Target next Friday (clinic is strictly closed on Friday/Saturday)
        var todayCairo = CairoTimeHelper.NowCairo.Date;
        int daysUntilFriday = ((int)DayOfWeek.Friday - (int)todayCairo.DayOfWeek + 7) % 7;
        if (daysUntilFriday == 0) daysUntilFriday = 7;
        var fridayDate = todayCairo.AddDays(daysUntilFriday).ToString("yyyy-MM-dd");

        var tool = new CheckAvailabilityTool(db);
        using var argsDoc = JsonDocument.Parse($"{{\"date\":\"{fridayDate}\",\"service\":\"كشف باطنة عامة\"}}");

        // Act
        var result = await tool.ExecuteAsync(argsDoc.RootElement, Guid.NewGuid(), CancellationToken.None);

        // Assert: Confirms closed day returns no slots for that specific day and suggests upcoming business slots
        Assert.True(result.Success);
        Assert.Contains("لا توجد مواعيد متاحة في تاريخ", result.Message);
        Assert.Contains("أقرب مواعيد أخرى متاحة", result.Message);
        Assert.NotNull(result.Data);

        var dataJson = JsonSerializer.Serialize(result.Data);
        using var doc = JsonDocument.Parse(dataJson);
        var slots = doc.RootElement.GetProperty("availableSlots");
        Assert.True(slots.GetArrayLength() > 0);
    }

    [Fact]
    public async Task CheckAvailabilityTool_WhenEntireSystemHasNoSlots_ReturnsEmptyAvailableSlots()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        // Do NOT seed slots; empty database
        var tool = new CheckAvailabilityTool(db);
        using var argsDoc = JsonDocument.Parse("{\"date\":\"2026-10-15\",\"service\":\"كشف باطنة عامة\"}");

        // Act
        var result = await tool.ExecuteAsync(argsDoc.RootElement, Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("لا توجد أي مواعيد متاحة حالياً", result.Message);
        Assert.NotNull(result.Data);

        var dataJson = JsonSerializer.Serialize(result.Data);
        using var doc = JsonDocument.Parse(dataJson);
        var slots = doc.RootElement.GetProperty("availableSlots");
        Assert.Equal(0, slots.GetArrayLength());
    }

    [Fact]
    public async Task StageBooking_Creates_PendingBooking_AwaitingConfirmation()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();
        var conversationId = Guid.NewGuid();

        var agent = await db.Agents.FirstAsync();
        db.Conversations.Add(new Conversation { Id = conversationId, AgentId = agent.Id });
        await db.SaveChangesAsync();

        var tool = new StageBookingTool(db);
        var slot = await db.AvailabilitySlots.FirstAsync();

        var json = JsonSerializer.Serialize(new
        {
            slotId = slot.Id,
            customerName = "أحمد علي",
            customerPhone = "01122334455",
            serviceName = "كشف باطنة عامة"
        });
        using var argsDoc = JsonDocument.Parse(json);

        // Act
        var result = await tool.ExecuteAsync(argsDoc.RootElement, conversationId, CancellationToken.None);

        // Assert: Staging succeeds and creates pending record with hash
        Assert.True(result.Success);
        Assert.Contains("بانتظار تأكيد العميل", result.Message);

        var pending = await db.PendingBookings.FirstOrDefaultAsync(pb => pb.ConversationId == conversationId);
        Assert.NotNull(pending);
        Assert.Equal("Pending", pending.Status);
        Assert.False(string.IsNullOrEmpty(pending.RequestHash));

        // No confirmed booking should exist yet
        var bookings = await db.Bookings.CountAsync();
        Assert.Equal(0, bookings);
    }

    [Fact]
    public async Task Confirmation_Requires_ValidPendingBooking()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();
        var conversationId = Guid.NewGuid();

        var confirmationService = new BookingConfirmationService(db);

        // Act: Attempt to confirm non-existent pending ID
        var result = await confirmationService.ConfirmPendingBookingAsync(
            conversationId,
            Guid.NewGuid(),
            null,
            CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("PENDING_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task Confirmation_CommitsBooking_AfterStaging()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();
        var conversationId = Guid.NewGuid();

        var agent = await db.Agents.FirstAsync();
        db.Conversations.Add(new Conversation { Id = conversationId, AgentId = agent.Id });
        await db.SaveChangesAsync();

        // 1. Stage booking
        var stageTool = new StageBookingTool(db);
        var slot = await db.AvailabilitySlots.FirstAsync();
        var stageJson = JsonSerializer.Serialize(new
        {
            slotId = slot.Id,
            customerName = "محمد عاطف",
            customerPhone = "01012345678",
            serviceName = "كشف باطنة عامة"
        });
        using var stageArgs = JsonDocument.Parse(stageJson);
        var stageResult = await stageTool.ExecuteAsync(stageArgs.RootElement, conversationId, CancellationToken.None);
        Assert.True(stageResult.Success);

        var pending = await db.PendingBookings.FirstAsync(pb => pb.ConversationId == conversationId);

        // 2. Explicit customer confirmation action through application service
        var confirmationService = new BookingConfirmationService(db);
        var confirmResult = await confirmationService.ConfirmPendingBookingAsync(
            conversationId,
            pending.Id,
            pending.RequestHash,
            CancellationToken.None);

        // Assert
        Assert.True(confirmResult.Success);
        Assert.Contains("تم تأكيد الحجز بنجاح", confirmResult.Message);

        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.CustomerPhone == "01012345678");
        Assert.NotNull(booking);
        Assert.Equal("Confirmed", booking.Status);
        Assert.Equal("محمد عاطف", booking.CustomerName);

        // Pending booking is marked Confirmed
        var updatedPending = await db.PendingBookings.FindAsync(pending.Id);
        Assert.Equal("Confirmed", updatedPending!.Status);

        // Slot capacity consumed
        var updatedSlot = await db.AvailabilitySlots.FindAsync(slot.Id);
        Assert.Equal(1, updatedSlot!.BookedCapacity);
    }

    [Fact]
    public async Task Confirmation_Idempotent_ReturnsOriginalBooking()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var agent = new Agent { Id = Guid.NewGuid(), Name = "Test", SystemPrompt = "Test", AllowedToolsJson = "[]" };
        db.Agents.Add(agent);

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            ServiceName = "كشف باطنة عامة",
            StartTimeUtc = DateTime.UtcNow.AddHours(2),
            EndTimeUtc = DateTime.UtcNow.AddHours(2.5),
            TotalCapacity = 2,
            BookedCapacity = 0
        };
        db.AvailabilitySlots.Add(slot);

        var conversationId = Guid.NewGuid();
        db.Conversations.Add(new Conversation { Id = conversationId, AgentId = agent.Id });
        await db.SaveChangesAsync();

        var stageTool = new StageBookingTool(db);
        var stageJson = JsonSerializer.Serialize(new
        {
            slotId = slot.Id,
            customerName = "محمد عاطف",
            customerPhone = "01012345678",
            serviceName = "كشف باطنة عامة"
        });
        using var stageArgs = JsonDocument.Parse(stageJson);
        var stageResult = await stageTool.ExecuteAsync(stageArgs.RootElement, conversationId, CancellationToken.None);
        Assert.True(stageResult.Success);

        var pending1 = await db.PendingBookings.FirstAsync(pb => pb.ConversationId == conversationId);

        var confirmationService = new BookingConfirmationService(db);
        var result1 = await confirmationService.ConfirmPendingBookingAsync(
            conversationId,
            pending1.Id,
            pending1.RequestHash,
            CancellationToken.None);
        Assert.True(result1.Success);

        // Stage again with SAME details
        using var stageArgs2 = JsonDocument.Parse(stageJson);
        var stageResult2 = await stageTool.ExecuteAsync(stageArgs2.RootElement, conversationId, CancellationToken.None);
        Assert.True(stageResult2.Success);

        var pending2 = await db.PendingBookings.OrderByDescending(pb => pb.CreatedAtUtc).FirstAsync();

        // Act: Confirm again under same idempotency key
        var result2 = await confirmationService.ConfirmPendingBookingAsync(
            conversationId,
            pending2.Id,
            pending2.RequestHash,
            CancellationToken.None);

        // Assert: Replays original booking
        Assert.True(result2.Success);
        Assert.Contains("مسجل بالفعل", result2.Message);

        var totalBookings = await db.Bookings.CountAsync(b => b.CustomerPhone == "01012345678");
        Assert.Equal(1, totalBookings);
    }

    [Fact]
    public async Task Confirmation_RejectsIdempotencyKeyReuse_WithDifferentDetails()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var agent = new Agent { Id = Guid.NewGuid(), Name = "Test", SystemPrompt = "Test", AllowedToolsJson = "[]" };
        db.Agents.Add(agent);

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            ServiceName = "كشف باطنة عامة",
            StartTimeUtc = DateTime.UtcNow.AddHours(2),
            EndTimeUtc = DateTime.UtcNow.AddHours(2.5),
            TotalCapacity = 2,
            BookedCapacity = 0
        };
        db.AvailabilitySlots.Add(slot);

        var conversationId = Guid.NewGuid();
        db.Conversations.Add(new Conversation { Id = conversationId, AgentId = agent.Id });
        await db.SaveChangesAsync();

        var stageTool = new StageBookingTool(db);
        var confirmationService = new BookingConfirmationService(db);

        // 1. Stage and confirm with original details
        var json1 = JsonSerializer.Serialize(new
        {
            slotId = slot.Id,
            customerName = "محمد عاطف",
            customerPhone = "01012345678",
            serviceName = "كشف باطنة عامة"
        });
        using var args1 = JsonDocument.Parse(json1);
        await stageTool.ExecuteAsync(args1.RootElement, conversationId, CancellationToken.None);
        var pending1 = await db.PendingBookings.FirstAsync(pb => pb.ConversationId == conversationId);

        var result1 = await confirmationService.ConfirmPendingBookingAsync(conversationId, pending1.Id, pending1.RequestHash, CancellationToken.None);
        Assert.True(result1.Success);

        // 2. Stage with DIFFERENT details (different name) -> same idempotency key (same conv+slot+phone)
        var json2 = JsonSerializer.Serialize(new
        {
            slotId = slot.Id,
            customerName = "أحمد علي", // Different name!
            customerPhone = "01012345678",
            serviceName = "كشف باطنة عامة"
        });
        using var args2 = JsonDocument.Parse(json2);
        await stageTool.ExecuteAsync(args2.RootElement, conversationId, CancellationToken.None);
        var pending2 = await db.PendingBookings.OrderByDescending(pb => pb.CreatedAtUtc).FirstAsync();

        // Act: Attempt to confirm with changed details
        var result2 = await confirmationService.ConfirmPendingBookingAsync(conversationId, pending2.Id, pending2.RequestHash, CancellationToken.None);

        // Assert: Rejected as IDEMPOTENCY_CONFLICT
        Assert.False(result2.Success);
        Assert.Equal("IDEMPOTENCY_CONFLICT", result2.ErrorCode);
    }

    [Fact]
    public async Task StageBooking_InvalidatesPreviousPending_WhenDetailsChange()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();
        var conversationId = Guid.NewGuid();

        var agent = await db.Agents.FirstAsync();
        db.Conversations.Add(new Conversation { Id = conversationId, AgentId = agent.Id });
        await db.SaveChangesAsync();

        var slots = await db.AvailabilitySlots.OrderBy(s => s.StartTimeUtc).Take(2).ToListAsync();
        Assert.True(slots.Count >= 2);

        var stageTool = new StageBookingTool(db);

        // Stage slot 1
        var json1 = JsonSerializer.Serialize(new
        {
            slotId = slots[0].Id,
            customerName = "محمد عاطف",
            customerPhone = "01012345678"
        });
        using var args1 = JsonDocument.Parse(json1);
        await stageTool.ExecuteAsync(args1.RootElement, conversationId, CancellationToken.None);

        // Stage slot 2 (changed details)
        var json2 = JsonSerializer.Serialize(new
        {
            slotId = slots[1].Id,
            customerName = "محمد عاطف",
            customerPhone = "01012345678"
        });
        using var args2 = JsonDocument.Parse(json2);
        await stageTool.ExecuteAsync(args2.RootElement, conversationId, CancellationToken.None);

        // Assert: First is invalidated, second is Pending
        var allPending = await db.PendingBookings
            .Where(pb => pb.ConversationId == conversationId)
            .OrderBy(pb => pb.CreatedAtUtc)
            .ToListAsync();

        Assert.Equal(2, allPending.Count);
        Assert.Equal("Invalidated", allPending[0].Status);
        Assert.Equal("Pending", allPending[1].Status);
    }

    // =========================================================================
    // Targeted Regression Tests for Prompt Requirements
    // =========================================================================

    [Fact]
    public async Task Regression_ModelAttemptsConfirmation_WithoutCustomerAction_NoBookingCommitted()
    {
        // Arrange: Agent only has [CheckAvailability, StageBooking, GetBooking]
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();

        var agent = await db.Agents.FirstAsync();
        var tools = new List<ITool>
        {
            new CheckAvailabilityTool(db),
            new StageBookingTool(db),
            new GetBookingTool(db)
        };
        var registry = new ToolRegistry(tools);

        // Provider that tries to invoke a tool named "ConfirmBooking"
        var illicitProvider = new MockAttemptConfirmationProvider();
        var orchestrator = new AgentOrchestrator(
            db,
            illicitProvider,
            registry,
            new MemoryCache(new MemoryCacheOptions()),
            new ConversationLockManager(),
            new InferenceThrottlingManager(),
            NullLogger<AgentOrchestrator>.Instance);

        var convId = Guid.NewGuid();

        // Act
        var events = new List<ChatEvent>();
        await foreach (var ev in orchestrator.ProcessUserMessageAsync(agent.Id, convId, "أكد الحجز يا موديل", CancellationToken.None))
        {
            events.Add(ev);
        }

        // Assert: Orchestrator intercepted and rejected unauthorized tool
        var toolResultEvent = events.FirstOrDefault(e => e.EventType == "tool_result");
        Assert.NotNull(toolResultEvent);
        var result = (ToolResult)toolResultEvent.Metadata!;
        Assert.False(result.Success);
        Assert.Equal("UNAUTHORIZED_TOOL", result.ErrorCode);

        // CRITICAL: Trust boundary maintained — zero bookings committed in database!
        var bookingsCount = await db.Bookings.CountAsync();
        Assert.Equal(0, bookingsCount);
    }

    [Fact]
    public async Task Regression_Confirmation_ReferencesDifferentConversation_Rejected()
    {
        // Arrange: Stage pending booking in Conversation A
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();

        var agent = await db.Agents.FirstAsync();
        var convA = Guid.NewGuid();
        var convB = Guid.NewGuid();

        db.Conversations.Add(new Conversation { Id = convA, AgentId = agent.Id });
        db.Conversations.Add(new Conversation { Id = convB, AgentId = agent.Id });
        await db.SaveChangesAsync();

        var slot = await db.AvailabilitySlots.FirstAsync();
        var stageTool = new StageBookingTool(db);
        var stageJson = JsonSerializer.Serialize(new
        {
            slotId = slot.Id,
            customerName = "علي حسن",
            customerPhone = "01055556666"
        });
        using var stageDoc = JsonDocument.Parse(stageJson);
        await stageTool.ExecuteAsync(stageDoc.RootElement, convA, CancellationToken.None);

        var pendingA = await db.PendingBookings.FirstAsync(pb => pb.ConversationId == convA);

        // Act: Attempt to confirm pendingA using Conversation B
        var confirmationService = new BookingConfirmationService(db);
        var result = await confirmationService.ConfirmPendingBookingAsync(
            convB, // Wrong conversation!
            pendingA.Id,
            pendingA.RequestHash,
            CancellationToken.None);

        // Assert: Cross-conversation confirmation strictly rejected
        Assert.False(result.Success);
        Assert.Equal("CONVERSATION_MISMATCH", result.ErrorCode);

        var bookingsCount = await db.Bookings.CountAsync();
        Assert.Equal(0, bookingsCount);
    }

    [Fact]
    public async Task Regression_DetailsChange_AfterConfirmationCardDisplayed_StaleActionRejected()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();
        var convId = Guid.NewGuid();

        var agent = await db.Agents.FirstAsync();
        db.Conversations.Add(new Conversation { Id = convId, AgentId = agent.Id });
        await db.SaveChangesAsync();

        var slots = await db.AvailabilitySlots.OrderBy(s => s.StartTimeUtc).Take(2).ToListAsync();
        var stageTool = new StageBookingTool(db);
        var confirmationService = new BookingConfirmationService(db);

        // Phase 1: Customer stages slot 0 -> displayed on card with pending1.Id and requestHash1
        var json1 = JsonSerializer.Serialize(new
        {
            slotId = slots[0].Id,
            customerName = "سالم",
            customerPhone = "01033334444"
        });
        using var doc1 = JsonDocument.Parse(json1);
        await stageTool.ExecuteAsync(doc1.RootElement, convId, CancellationToken.None);
        var pending1 = await db.PendingBookings.FirstAsync(pb => pb.ConversationId == convId);

        // Phase 2: Customer changes details to slot 1 -> pending1 is invalidated, pending2 is created
        var json2 = JsonSerializer.Serialize(new
        {
            slotId = slots[1].Id,
            customerName = "سالم",
            customerPhone = "01033334444"
        });
        using var doc2 = JsonDocument.Parse(json2);
        await stageTool.ExecuteAsync(doc2.RootElement, convId, CancellationToken.None);

        // Act: Customer clicks the stale confirmation card for pending1
        var result = await confirmationService.ConfirmPendingBookingAsync(
            convId,
            pending1.Id,
            pending1.RequestHash,
            CancellationToken.None);

        // Assert: Stale card is rejected
        Assert.False(result.Success);
        Assert.Equal("STALE_PENDING_BOOKING", result.ErrorCode);

        // Slot 0 capacity was NOT consumed
        var slot0 = await db.AvailabilitySlots.FindAsync(slots[0].Id);
        Assert.Equal(0, slot0!.BookedCapacity);
    }

    [Fact]
    public async Task Regression_ConcurrentConfirmations_SamePendingBooking_OneBooking_OneCapacityIncrement()
    {
        // Arrange: Slot with capacity = 1
        var dbFile = $"test_concurr_{Guid.NewGuid():N}.db";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbFile}")
            .Options;

        var slotId = Guid.NewGuid();
        var convId = Guid.NewGuid();

        using (var setupDb = new AppDbContext(options))
        {
            setupDb.Database.EnsureCreated();
            var agent = new Agent { Id = Guid.NewGuid(), Name = "Test", SystemPrompt = "Test", AllowedToolsJson = "[]" };
            setupDb.Agents.Add(agent);

            var slot = new AvailabilitySlot
            {
                Id = slotId,
                ServiceName = "كشف باطنة",
                StartTimeUtc = DateTime.UtcNow.AddHours(2),
                EndTimeUtc = DateTime.UtcNow.AddHours(2.5),
                TotalCapacity = 1,
                BookedCapacity = 0
            };
            setupDb.AvailabilitySlots.Add(slot);

            setupDb.Conversations.Add(new Conversation { Id = convId, AgentId = agent.Id });
            await setupDb.SaveChangesAsync();

            var stageTool = new StageBookingTool(setupDb);
            var stageJson = JsonSerializer.Serialize(new
            {
                slotId = slot.Id,
                customerName = "إبراهيم",
                customerPhone = "01077778888",
                serviceName = "كشف باطنة"
            });
            using var doc = JsonDocument.Parse(stageJson);
            await stageTool.ExecuteAsync(doc.RootElement, convId, CancellationToken.None);
        }

        Guid pendingId;
        string pendingHash;
        using (var readDb = new AppDbContext(options))
        {
            var p = await readDb.PendingBookings.FirstAsync(pb => pb.ConversationId == convId);
            pendingId = p.Id;
            pendingHash = p.RequestHash;
        }

        // Act: Two concurrent confirmations with independent DbContexts simulating 2 concurrent HTTP requests
        var task1 = Task.Run(async () =>
        {
            using var taskDb1 = new AppDbContext(options);
            var service1 = new BookingConfirmationService(taskDb1);
            return await service1.ConfirmPendingBookingAsync(convId, pendingId, pendingHash, CancellationToken.None);
        });

        var task2 = Task.Run(async () =>
        {
            using var taskDb2 = new AppDbContext(options);
            var service2 = new BookingConfirmationService(taskDb2);
            return await service2.ConfirmPendingBookingAsync(convId, pendingId, pendingHash, CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        // Assert: Both return success (one creates booking, the other replays duplicate)
        Assert.True(results[0].Success, $"Task 1 failed: {results[0].Message}");
        Assert.True(results[1].Success, $"Task 2 failed: {results[1].Message}");

        // Exactly 1 booking was created
        using (var verifyDb = new AppDbContext(options))
        {
            var bookings = await verifyDb.Bookings.Where(b => b.CustomerPhone == "01077778888").ToListAsync();
            Assert.Single(bookings);

            // Capacity was incremented by exactly 1
            var finalSlot = await verifyDb.AvailabilitySlots.FindAsync(slotId);
            Assert.Equal(1, finalSlot!.BookedCapacity);
        }
    }

    [Fact]
    public async Task Regression_StreamCancellation_ReachesUpstream_LeavesPersistedState()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        await db.SeedInitialDataAsync();

        var agent = await db.Agents.FirstAsync();
        var tools = new List<ITool>
        {
            new CheckAvailabilityTool(db),
            new StageBookingTool(db),
            new GetBookingTool(db)
        };
        var registry = new ToolRegistry(tools);
        var fakeLlm = new DeterministicFakeLlmProvider();
        var orchestrator = new AgentOrchestrator(
            db,
            fakeLlm,
            registry,
            new MemoryCache(new MemoryCacheOptions()),
            new ConversationLockManager(),
            new InferenceThrottlingManager(),
            NullLogger<AgentOrchestrator>.Instance);

        var convId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        // Act: Cancel the token during streaming
        var tokensReceived = 0;
        try
        {
            await foreach (var ev in orchestrator.ProcessUserMessageAsync(agent.Id, convId, "إيه المواعيد المتاحة بكرة؟", cts.Token))
            {
                tokensReceived++;
                if (tokensReceived >= 1)
                {
                    cts.Cancel(); // Simulate client aborting connection
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation
        }

        // Assert: Conversation and user message are cleanly persisted in database
        var conversation = await db.Conversations.Include(c => c.Messages).FirstOrDefaultAsync(c => c.Id == convId);
        Assert.NotNull(conversation);
        Assert.Equal("Active", conversation.Status);

        var userMessage = conversation.Messages.FirstOrDefault(m => m.Role == "user");
        Assert.NotNull(userMessage);
        Assert.Equal("إيه المواعيد المتاحة بكرة؟", userMessage.Content);
    }

    private class MockAttemptConfirmationProvider : ILlmProvider
    {
        public string ProviderName => "MockAttemptConfirmation";

        public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(LlmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new LlmStreamChunk(
                ToolCalls: new List<LlmToolCall>
                {
                    new("call_confirm", "ConfirmBooking", "{}")
                },
                IsCompleted: true
            );
        }
    }
}
