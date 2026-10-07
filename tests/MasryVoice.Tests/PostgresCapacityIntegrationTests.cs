using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Features.Bookings;
using MasryVoice.Api.Features.Tools;
using MasryVoice.Api.Infrastructure.Persistence;
using Xunit;

namespace MasryVoice.Tests;

/// <summary>
/// PostgreSQL Integration Tests for Multi-Connection Concurrent Capacity Protection.
/// These tests connect to the real PostgreSQL container (pgvector / postgres 16)
/// using independent connections and concurrent requests to verify that row-level locking (FOR UPDATE)
/// and database constraints strictly prevent overbooking without relying on an in-process lock.
/// </summary>
[Trait("Category", "PostgresIntegration")]
public class PostgresCapacityIntegrationTests
{
    private static readonly string PostgresConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSql") ??
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ??
        "Host=localhost;Port=5432;Database=masryvoice_db;Username=masryvoice;Password=masryvoice_secret_pass;Include Error Detail=true;";

    private AppDbContext CreatePostgresDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresConnectionString, o => o.UseVector())
            .Options;

        return new AppDbContext(options);
    }

    private async Task EnsurePostgresInitializedAsync()
    {
        using var db = CreatePostgresDbContext();
        try
        {
            await DatabaseMigrationHelper.ApplyMigrationsAsync(db);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("RepairPartiallyUpgradedSchemaAsync"))
        {
            await DatabaseMigrationHelper.RepairPartiallyUpgradedSchemaAsync(db);
        }
        await db.SeedInitialDataAsync();
    }

    [Fact]
    public async Task Postgres_ConcurrentRequests_PreventOverbooking_Capacity1()
    {
        // Arrange
        await EnsurePostgresInitializedAsync();

        var agentId = Guid.NewGuid();
        var slotId = Guid.NewGuid();

        // Setup test agent and slot with TotalCapacity = 1
        using (var setupDb = CreatePostgresDbContext())
        {
            var agent = new Agent
            {
                Id = agentId,
                Name = "Postgres Concurrency Agent",
                SystemPrompt = "Test",
                AllowedToolsJson = "[\"ConfirmBooking\"]"
            };
            setupDb.Agents.Add(agent);

            var slot = new AvailabilitySlot
            {
                Id = slotId,
                ServiceName = "كشف باطنة - اختبار التزامن",
                StartTimeUtc = DateTime.UtcNow.AddDays(2).Date.AddHours(10),
                EndTimeUtc = DateTime.UtcNow.AddDays(2).Date.AddHours(10.5),
                TotalCapacity = 1,
                BookedCapacity = 0
            };
            setupDb.AvailabilitySlots.Add(slot);
            await setupDb.SaveChangesAsync();
        }

        // Stage 5 separate conversations with pending bookings
        const int concurrentClients = 5;
        var conversationIds = new List<Guid>();

        for (int i = 1; i <= concurrentClients; i++)
        {
            var convId = Guid.NewGuid();
            conversationIds.Add(convId);

            using var stageDb = CreatePostgresDbContext();
            stageDb.Conversations.Add(new Conversation { Id = convId, AgentId = agentId });

            var stageTool = new StageBookingTool(stageDb);
            var stagePayload = JsonSerializer.Serialize(new
            {
                slotId = slotId,
                customerName = $"مريض تزامن {i}",
                customerPhone = $"0109999000{i}",
                serviceName = "كشف باطنة - اختبار التزامن"
            });
            using var doc = JsonDocument.Parse(stagePayload);
            var stageResult = await stageTool.ExecuteAsync(doc.RootElement, convId, CancellationToken.None);
            Assert.True(stageResult.Success, $"Staging client {i} failed: {stageResult.Message}");
        }

        // Retrieve pending booking IDs
        var pendingBookings = new List<PendingBooking>();
        using (var fetchDb = CreatePostgresDbContext())
        {
            pendingBookings = await fetchDb.PendingBookings
                .Where(pb => conversationIds.Contains(pb.ConversationId) && pb.Status == "Pending")
                .ToListAsync();
        }

        // Act: Execute 5 concurrent confirmations simultaneously on independent connections
        var confirmTasks = pendingBookings.Select(async pb =>
        {
            // Each task has its OWN INDEPENDENT DbContext and DB connection
            using var taskDb = CreatePostgresDbContext();
            var confirmService = new BookingConfirmationService(taskDb);
            return await confirmService.ConfirmPendingBookingAsync(pb.ConversationId, pb.Id, pb.RequestHash, CancellationToken.None);
        }).ToArray();

        var results = await Task.WhenAll(confirmTasks);

        // Assert:
        // 1. Exactly 1 request must succeed
        var successfulResults = results.Where(r => r.Success).ToList();
        var capacityFailedResults = results.Where(r => !r.Success && r.ErrorCode == "SLOT_FULLY_BOOKED").ToList();

        Assert.Single(successfulResults);
        Assert.Equal(concurrentClients - 1, capacityFailedResults.Count);

        // 2. Verify state directly in PostgreSQL with a fresh independent connection
        using (var verifyDb = CreatePostgresDbContext())
        {
            var finalSlot = await verifyDb.AvailabilitySlots.FindAsync(slotId);
            Assert.NotNull(finalSlot);
            Assert.Equal(1, finalSlot.BookedCapacity);

            var actualBookings = await verifyDb.Bookings.Where(b => b.SlotId == slotId).ToListAsync();
            Assert.Single(actualBookings);

            var confirmedPending = await verifyDb.PendingBookings
                .Where(pb => pb.SlotId == slotId && pb.Status == "Confirmed")
                .ToListAsync();
            Assert.Single(confirmedPending);

            var failedPending = await verifyDb.PendingBookings
                .Where(pb => pb.SlotId == slotId && pb.Status == "FailedCapacity")
                .ToListAsync();
            Assert.Equal(concurrentClients - 1, failedPending.Count);
        }
    }

    [Fact]
    public async Task Postgres_ConcurrentRequests_PreventOverbooking_Capacity3()
    {
        // Arrange
        await EnsurePostgresInitializedAsync();

        var agentId = Guid.NewGuid();
        var slotId = Guid.NewGuid();

        // Setup test agent and slot with TotalCapacity = 3
        using (var setupDb = CreatePostgresDbContext())
        {
            var agent = new Agent
            {
                Id = agentId,
                Name = "Postgres Capacity 3 Agent",
                SystemPrompt = "Test",
                AllowedToolsJson = "[\"ConfirmBooking\"]"
            };
            setupDb.Agents.Add(agent);

            var slot = new AvailabilitySlot
            {
                Id = slotId,
                ServiceName = "كشف أطفال - اختبار سعة 3",
                StartTimeUtc = DateTime.UtcNow.AddDays(3).Date.AddHours(14),
                EndTimeUtc = DateTime.UtcNow.AddDays(3).Date.AddHours(14.5),
                TotalCapacity = 3,
                BookedCapacity = 0
            };
            setupDb.AvailabilitySlots.Add(slot);
            await setupDb.SaveChangesAsync();
        }

        // Stage 8 concurrent requests for the slot of capacity 3
        const int concurrentClients = 8;
        var conversationIds = new List<Guid>();

        for (int i = 1; i <= concurrentClients; i++)
        {
            var convId = Guid.NewGuid();
            conversationIds.Add(convId);

            using var stageDb = CreatePostgresDbContext();
            stageDb.Conversations.Add(new Conversation { Id = convId, AgentId = agentId });

            var stageTool = new StageBookingTool(stageDb);
            var stagePayload = JsonSerializer.Serialize(new
            {
                slotId = slotId,
                customerName = $"مريض سعة3 رقم {i}",
                customerPhone = $"0108888000{i}",
                serviceName = "كشف أطفال - اختبار سعة 3"
            });
            using var doc = JsonDocument.Parse(stagePayload);
            var stageResult = await stageTool.ExecuteAsync(doc.RootElement, convId, CancellationToken.None);
            Assert.True(stageResult.Success, $"Staging client {i} failed: {stageResult.Message}");
        }

        var pendingBookings = new List<PendingBooking>();
        using (var fetchDb = CreatePostgresDbContext())
        {
            pendingBookings = await fetchDb.PendingBookings
                .Where(pb => conversationIds.Contains(pb.ConversationId) && pb.Status == "Pending")
                .ToListAsync();
        }

        // Act: Execute 8 concurrent confirmations on independent DB connections
        var confirmTasks = pendingBookings.Select(async pb =>
        {
            using var taskDb = CreatePostgresDbContext();
            var confirmService = new BookingConfirmationService(taskDb);
            return await confirmService.ConfirmPendingBookingAsync(pb.ConversationId, pb.Id, pb.RequestHash, CancellationToken.None);
        }).ToArray();

        var results = await Task.WhenAll(confirmTasks);

        // Assert:
        // Exactly 3 succeed, exactly 5 fail with SLOT_FULLY_BOOKED
        var successfulResults = results.Where(r => r.Success).ToList();
        var capacityFailedResults = results.Where(r => !r.Success && r.ErrorCode == "SLOT_FULLY_BOOKED").ToList();

        Assert.Equal(3, successfulResults.Count);
        Assert.Equal(5, capacityFailedResults.Count);

        // Verify database state in PostgreSQL
        using (var verifyDb = CreatePostgresDbContext())
        {
            var finalSlot = await verifyDb.AvailabilitySlots.FindAsync(slotId);
            Assert.NotNull(finalSlot);
            Assert.Equal(3, finalSlot.BookedCapacity);

            var actualBookings = await verifyDb.Bookings.Where(b => b.SlotId == slotId).ToListAsync();
            Assert.Equal(3, actualBookings.Count);
        }
    }

    [Fact]
    public async Task Postgres_CheckConstraint_HardBackstop_RejectsDirectOverbooking()
    {
        // Arrange
        await EnsurePostgresInitializedAsync();

        var slotId = Guid.NewGuid();
        using (var setupDb = CreatePostgresDbContext())
        {
            var slot = new AvailabilitySlot
            {
                Id = slotId,
                ServiceName = "اختبار قيد قاعدة البيانات",
                StartTimeUtc = DateTime.UtcNow.AddDays(4),
                EndTimeUtc = DateTime.UtcNow.AddDays(4).AddHours(1),
                TotalCapacity = 1,
                BookedCapacity = 1 // Already full
            };
            setupDb.AvailabilitySlots.Add(slot);
            await setupDb.SaveChangesAsync();
        }

        // Act & Assert: Attempting to directly update BookedCapacity > TotalCapacity
        // must be rejected by PostgreSQL CHECK constraint CK_AvailabilitySlots_Capacity
        using (var updateDb = CreatePostgresDbContext())
        {
            var slot = await updateDb.AvailabilitySlots.FindAsync(slotId);
            Assert.NotNull(slot);
            slot.BookedCapacity = 2; // Exceeds TotalCapacity (1)

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => updateDb.SaveChangesAsync());
            Assert.NotNull(ex.InnerException);
            Assert.Contains("CK_AvailabilitySlots_Capacity", ex.InnerException.Message);
        }
    }
}
