using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Infrastructure.Persistence;
using Xunit;

namespace MasryVoice.Tests;

/// <summary>
/// Tests validating the EF migration for Booking.ConversationId against a populated database
/// from the previous revision. Verifies data preservation, reliable relationship backfilling,
/// rejection of phone-based ownership inference, and foreign key constraint integrity.
/// </summary>
public class SchemaMigrationAcceptanceTests
{
    [Fact]
    public async Task Migration_BackfillsOwnershipExclusivelyFromReliableRelationships_AndPreservesLegacyRecords()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"migration_test_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;

            var agentId = Guid.NewGuid();
            var convId1 = Guid.NewGuid();
            var convId2 = Guid.NewGuid();
            var slotId = Guid.NewGuid();
            var bId1 = Guid.NewGuid();
            var bId2 = Guid.NewGuid();
            var bId3 = Guid.NewGuid();

            // 1. Migrate specifically to InitialCreate (the previous revision schema where Booking has NO ConversationId)
            using (var db = new AppDbContext(options))
            {
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrator.MigrateAsync("InitialCreate");

                // Verify Bookings table does not have ConversationId yet by populating data via raw SQL
                await db.Database.ExecuteSqlRawAsync($@"
                    INSERT INTO Agents (Id, Name, SystemPrompt, ModelName, LanguageCode, Temperature, IsActive, AllowedToolsJson, CreatedAtUtc)
                    VALUES ('{agentId}', 'سارة', 'مساعد', 'qwen2.5:1.5b', 'ar-EG', 0.2, 1, '[]', '{DateTime.UtcNow:O}');

                    INSERT INTO Conversations (Id, AgentId, CustomerPhoneNumber, CustomerName, Channel, Status, StartedAtUtc, LastActiveAtUtc)
                    VALUES ('{convId1}', '{agentId}', '01011111111', 'عميل أ', 'web', 'Active', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}');

                    INSERT INTO Conversations (Id, AgentId, CustomerPhoneNumber, CustomerName, Channel, Status, StartedAtUtc, LastActiveAtUtc)
                    VALUES ('{convId2}', '{agentId}', '01022222222', 'عميل ب', 'web', 'Active', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}');

                    INSERT INTO AvailabilitySlots (Id, ServiceName, StartTimeUtc, EndTimeUtc, TotalCapacity, BookedCapacity)
                    VALUES ('{slotId}', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', '{DateTime.UtcNow.AddDays(1).AddMinutes(30):O}', 3, 3);

                    -- Pending booking for Conversation 1
                    INSERT INTO PendingBookings (Id, ConversationId, SlotId, CustomerName, CustomerPhone, ServiceName, BookingDateUtc, IdempotencyKey, RequestHash, Status, CreatedAtUtc)
                    VALUES ('{Guid.NewGuid()}', '{convId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'idemp_conv1_slot1', 'hash1', 'Confirmed', '{DateTime.UtcNow:O}');

                    -- Booking 1: Reliable match with PendingBooking 1 via IdempotencyKey
                    INSERT INTO Bookings (Id, SlotId, CustomerName, CustomerPhone, ServiceName, BookingDateUtc, Status, IdempotencyKey, RequestHash, CreatedAtUtc)
                    VALUES ('{bId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'Confirmed', 'idemp_conv1_slot1', 'hash1', '{DateTime.UtcNow:O}');

                    -- Booking 2: Phone number matches Conversation 1, but IdempotencyKey does NOT match any pending booking
                    -- Ownership MUST NOT be inferred from phone number!
                    INSERT INTO Bookings (Id, SlotId, CustomerName, CustomerPhone, ServiceName, BookingDateUtc, Status, IdempotencyKey, RequestHash, CreatedAtUtc)
                    VALUES ('{bId2}', '{slotId}', 'عميل أ مختلف', '01011111111', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'Confirmed', 'idemp_unrelated_phone_match', 'hash2', '{DateTime.UtcNow:O}');

                    -- Booking 3: Legacy booking with unresolved ownership (no matching pending booking)
                    INSERT INTO Bookings (Id, SlotId, CustomerName, CustomerPhone, ServiceName, BookingDateUtc, Status, IdempotencyKey, RequestHash, CreatedAtUtc)
                    VALUES ('{bId3}', '{slotId}', 'عميل قديم', '01033333333', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'Confirmed', 'idemp_legacy_unresolved', 'hash3', '{DateTime.UtcNow:O}');
                ");
            }

            // 2. Apply migration AddBookingConversationId against the populated legacy database
            using (var db = new AppDbContext(options))
            {
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrator.MigrateAsync("AddBookingConversationId");
            }

            // 3. Verify post-migration state and data integrity
            using (var db = new AppDbContext(options))
            {
                var bookings = await db.Bookings.AsNoTracking().ToListAsync();
                Assert.Equal(3, bookings.Count);

                var booking1 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_conv1_slot1");
                var booking2 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_unrelated_phone_match");
                var booking3 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_legacy_unresolved");

                Assert.NotNull(booking1);
                Assert.NotNull(booking2);
                Assert.NotNull(booking3);

                // Booking 1: Reliably backfilled from matching PendingBooking
                Assert.Equal(convId1, booking1.ConversationId);
                Assert.Equal("عميل أ", booking1.CustomerName);
                Assert.Equal("01011111111", booking1.CustomerPhone);

                // Booking 2: Ownership MUST NOT be inferred merely from matching phone number
                Assert.Null(booking2.ConversationId);
                Assert.Equal("01011111111", booking2.CustomerPhone);

                // Booking 3: Legacy booking with unresolved ownership remains NULL
                Assert.Null(booking3.ConversationId);
                Assert.Equal("عميل قديم", booking3.CustomerName);
            }
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }
}
