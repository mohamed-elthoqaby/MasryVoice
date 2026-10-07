using Microsoft.EntityFrameworkCore;
using MasryVoice.Api.Infrastructure.Persistence;
using Xunit;

namespace MasryVoice.Tests;

/// <summary>
/// Acceptance tests validating provider-correct schema migrations and trustworthy baseline handling
/// across both PostgreSQL and SQLite. Verifies fresh initialization, rejection of corrupted/partially
/// upgraded databases, safe baselining of unversioned databases matching commit 428f490, unambiguous
/// ownership backfilling, capacity preservation, and idempotent repeated execution.
/// </summary>
public class SchemaMigrationAcceptanceTests
{
    private static readonly string BasePostgresConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSql") ??
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ??
        "Host=localhost;Port=5432;Database=masryvoice_db;Username=masryvoice;Password=masryvoice_secret_pass;Include Error Detail=true;";

    private static string GetAdminConnectionString()
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder(BasePostgresConnectionString)
        {
            Database = "postgres"
        };
        return b.ConnectionString;
    }

    private static async Task<string> CreateDisposablePostgresDatabaseAsync()
    {
        var dbName = $"masry_mig_{Guid.NewGuid():N}";
        await using var conn = new Npgsql.NpgsqlConnection(GetAdminConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\";";
        await cmd.ExecuteNonQueryAsync();

        var b = new Npgsql.NpgsqlConnectionStringBuilder(BasePostgresConnectionString)
        {
            Database = dbName
        };
        return b.ConnectionString;
    }

    private static async Task DropDisposablePostgresDatabaseAsync(string connectionString)
    {
        try
        {
            var b = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            var dbName = b.Database;
            await using var conn = new Npgsql.NpgsqlConnection(GetAdminConnectionString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);";
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    private static async Task CreateLegacyPostgresSchema428F490Async(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE EXTENSION IF NOT EXISTS vector;

            CREATE TABLE IF NOT EXISTS "Agents" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Name" character varying(150) NOT NULL,
                "SystemPrompt" text NOT NULL,
                "ModelName" character varying(100) NOT NULL,
                "LanguageCode" character varying(10) NOT NULL,
                "Temperature" double precision NOT NULL,
                "IsActive" boolean NOT NULL,
                "AllowedToolsJson" text NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "Conversations" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "AgentId" uuid NOT NULL REFERENCES "Agents"("Id") ON DELETE RESTRICT,
                "CustomerPhoneNumber" text,
                "CustomerName" text,
                "Channel" text NOT NULL,
                "Status" text NOT NULL,
                "StartedAtUtc" timestamp with time zone NOT NULL,
                "LastActiveAtUtc" timestamp with time zone NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "Messages" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ConversationId" uuid NOT NULL REFERENCES "Conversations"("Id") ON DELETE CASCADE,
                "Role" text NOT NULL,
                "Content" text NOT NULL,
                "SequenceNumber" integer NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_Messages_ConversationId_SequenceNumber" ON "Messages" ("ConversationId", "SequenceNumber");

            CREATE TABLE IF NOT EXISTS "ToolExecutions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ConversationId" uuid NOT NULL REFERENCES "Conversations"("Id") ON DELETE CASCADE,
                "ToolName" text NOT NULL,
                "ArgumentsJson" text NOT NULL,
                "ResultJson" text NOT NULL,
                "Status" text NOT NULL,
                "DurationMs" bigint NOT NULL,
                "ExecutedAtUtc" timestamp with time zone NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ToolExecutions_ConversationId_ExecutedAtUtc" ON "ToolExecutions" ("ConversationId", "ExecutedAtUtc");

            CREATE TABLE IF NOT EXISTS "AvailabilitySlots" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ServiceName" character varying(150) NOT NULL,
                "StartTimeUtc" timestamp with time zone NOT NULL,
                "EndTimeUtc" timestamp with time zone NOT NULL,
                "TotalCapacity" integer NOT NULL,
                "BookedCapacity" integer NOT NULL,
                CONSTRAINT "CK_AvailabilitySlots_Capacity" CHECK ("BookedCapacity" <= "TotalCapacity")
            );
            CREATE INDEX IF NOT EXISTS "IX_AvailabilitySlots_StartTimeUtc" ON "AvailabilitySlots" ("StartTimeUtc");

            CREATE TABLE IF NOT EXISTS "PendingBookings" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ConversationId" uuid NOT NULL REFERENCES "Conversations"("Id") ON DELETE CASCADE,
                "SlotId" uuid NOT NULL REFERENCES "AvailabilitySlots"("Id") ON DELETE RESTRICT,
                "CustomerName" text NOT NULL,
                "CustomerPhone" text NOT NULL,
                "ServiceName" text NOT NULL,
                "BookingDateUtc" timestamp with time zone NOT NULL,
                "IdempotencyKey" text NOT NULL,
                "RequestHash" text NOT NULL,
                "Status" text NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "ConfirmedAtUtc" timestamp with time zone
            );
            CREATE INDEX IF NOT EXISTS "IX_PendingBookings_ConversationId_Status" ON "PendingBookings" ("ConversationId", "Status");
            CREATE INDEX IF NOT EXISTS "IX_PendingBookings_CreatedAtUtc" ON "PendingBookings" ("CreatedAtUtc");

            CREATE TABLE IF NOT EXISTS "Bookings" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "SlotId" uuid NOT NULL REFERENCES "AvailabilitySlots"("Id") ON DELETE RESTRICT,
                "CustomerName" text NOT NULL,
                "CustomerPhone" text NOT NULL,
                "ServiceName" text NOT NULL,
                "BookingDateUtc" timestamp with time zone NOT NULL,
                "Status" text NOT NULL,
                "IdempotencyKey" text NOT NULL,
                "RequestHash" text NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Bookings_IdempotencyKey" ON "Bookings" ("IdempotencyKey");
            CREATE INDEX IF NOT EXISTS "IX_Bookings_CreatedAtUtc" ON "Bookings" ("CreatedAtUtc");

            CREATE TABLE IF NOT EXISTS "KnowledgeDocuments" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Title" character varying(250) NOT NULL,
                "FileName" text NOT NULL,
                "Category" character varying(100) NOT NULL,
                "ChunkCount" integer NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_KnowledgeDocuments_CreatedAtUtc" ON "KnowledgeDocuments" ("CreatedAtUtc");

            CREATE TABLE IF NOT EXISTS "DocumentChunks" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "DocumentId" uuid NOT NULL REFERENCES "KnowledgeDocuments"("Id") ON DELETE CASCADE,
                "ChunkIndex" integer NOT NULL,
                "Content" text NOT NULL,
                "Embedding" vector(384),
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_DocumentChunks_DocumentId" ON "DocumentChunks" ("DocumentId");

            CREATE TABLE IF NOT EXISTS "OutboxJobs" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Topic" character varying(100) NOT NULL,
                "PayloadJson" text NOT NULL,
                "Status" character varying(50) NOT NULL,
                "RetryCount" integer NOT NULL,
                "MaxRetries" integer NOT NULL,
                "NextRetryUtc" timestamp with time zone NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "ProcessedAtUtc" timestamp with time zone,
                "LastError" text
            );
            CREATE INDEX IF NOT EXISTS "IX_OutboxJobs_Status_NextRetryUtc" ON "OutboxJobs" ("Status", "NextRetryUtc");
        """);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Postgres_FreshInitialization_AppliesAllMigrationsAndEnablesVectorExtension()
    {
        var cs = await CreateDisposablePostgresDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(cs, o => o.UseVector())
                .Options;

            using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationHelper.ApplyMigrationsAsync(db);

                // Verify pgvector extension is enabled
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT COUNT(1) FROM pg_extension WHERE extname = 'vector';";
                if (cmd.Connection?.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync();
                var vectorCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                Assert.Equal(1, vectorCount);

                // Verify check constraint on AvailabilitySlots exists
                await using var cmdCheck = db.Database.GetDbConnection().CreateCommand();
                cmdCheck.CommandText = "SELECT COUNT(1) FROM information_schema.check_constraints WHERE constraint_name = 'CK_AvailabilitySlots_Capacity';";
                var checkCount = Convert.ToInt64(await cmdCheck.ExecuteScalarAsync());
                Assert.Equal(1, checkCount);

                // Verify all migrations applied in history
                var applied = await db.Database.GetAppliedMigrationsAsync();
                Assert.Contains(applied, m => m.EndsWith("_InitialCreate"));
                Assert.Contains(applied, m => m.EndsWith("_AddBookingConversationId"));
            }
        }
        finally
        {
            await DropDisposablePostgresDatabaseAsync(cs);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Postgres_UpgradePopulatedLegacyDatabase_EnsureCreated_PreservesData_AndBackfillsReliably()
    {
        var cs = await CreateDisposablePostgresDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(cs, o => o.UseVector())
                .Options;

            var agentId = Guid.NewGuid();
            var convId1 = Guid.NewGuid();
            var convId2 = Guid.NewGuid();
            var slotId = Guid.NewGuid();
            var slotId2 = Guid.NewGuid();
            var bId1 = Guid.NewGuid();
            var bId2 = Guid.NewGuid();
            var bId3 = Guid.NewGuid();
            var bId4 = Guid.NewGuid();
            var bIdConflictSlot = Guid.NewGuid();
            var bIdConflictHash = Guid.NewGuid();

            // 1. Setup independently preserved legacy schema corresponding to commit 428f490 via raw DDL
            using (var db = new AppDbContext(options))
            {
                await CreateLegacyPostgresSchema428F490Async(db);

                // Populate representative legacy data
                await db.Database.ExecuteSqlRawAsync($@"
                    INSERT INTO ""Agents"" (""Id"", ""Name"", ""SystemPrompt"", ""ModelName"", ""LanguageCode"", ""Temperature"", ""IsActive"", ""AllowedToolsJson"", ""CreatedAtUtc"")
                    VALUES ('{agentId}', 'سارة', 'مساعد', 'qwen2.5:1.5b', 'ar-EG', 0.2, true, '[]', NOW());

                    INSERT INTO ""Conversations"" (""Id"", ""AgentId"", ""CustomerPhoneNumber"", ""CustomerName"", ""Channel"", ""Status"", ""StartedAtUtc"", ""LastActiveAtUtc"")
                    VALUES ('{convId1}', '{agentId}', '01011111111', 'عميل أ', 'web', 'Active', NOW(), NOW());

                    INSERT INTO ""Conversations"" (""Id"", ""AgentId"", ""CustomerPhoneNumber"", ""CustomerName"", ""Channel"", ""Status"", ""StartedAtUtc"", ""LastActiveAtUtc"")
                    VALUES ('{convId2}', '{agentId}', '01022222222', 'عميل ب', 'web', 'Active', NOW(), NOW());

                    INSERT INTO ""AvailabilitySlots"" (""Id"", ""ServiceName"", ""StartTimeUtc"", ""EndTimeUtc"", ""TotalCapacity"", ""BookedCapacity"")
                    VALUES ('{slotId}', 'كشف باطنة', NOW() + INTERVAL '1 day', NOW() + INTERVAL '1 day 30 minutes', 5, 4);

                    INSERT INTO ""AvailabilitySlots"" (""Id"", ""ServiceName"", ""StartTimeUtc"", ""EndTimeUtc"", ""TotalCapacity"", ""BookedCapacity"")
                    VALUES ('{slotId2}', 'كشف أطفال', NOW() + INTERVAL '2 days', NOW() + INTERVAL '2 days 30 minutes', 3, 1);

                    -- Pending booking 1: Unambiguous match for Conversation 1
                    INSERT INTO ""PendingBookings"" (""Id"", ""ConversationId"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""IdempotencyKey"", ""RequestHash"", ""Status"", ""CreatedAtUtc"")
                    VALUES ('{Guid.NewGuid()}', '{convId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'idemp_unambiguous_1', 'hash1', 'Confirmed', NOW());

                    -- Conflicting/Ambiguous Pending bookings: TWO pending bookings with same IdempotencyKey but DIFFERENT conversations
                    INSERT INTO ""PendingBookings"" (""Id"", ""ConversationId"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""IdempotencyKey"", ""RequestHash"", ""Status"", ""CreatedAtUtc"")
                    VALUES ('{Guid.NewGuid()}', '{convId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'idemp_conflicting_2', 'hash2a', 'Confirmed', NOW());
                    INSERT INTO ""PendingBookings"" (""Id"", ""ConversationId"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""IdempotencyKey"", ""RequestHash"", ""Status"", ""CreatedAtUtc"")
                    VALUES ('{Guid.NewGuid()}', '{convId2}', '{slotId}', 'عميل ب', '01022222222', 'كشف باطنة', NOW() + INTERVAL '1 day', 'idemp_conflicting_2', 'hash2b', 'Confirmed', NOW());

                    -- Conflicting slot case: Pending booking has slotId2 while booking has slotId (same conversation)
                    INSERT INTO ""PendingBookings"" (""Id"", ""ConversationId"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""IdempotencyKey"", ""RequestHash"", ""Status"", ""CreatedAtUtc"")
                    VALUES ('{Guid.NewGuid()}', '{convId1}', '{slotId2}', 'عميل أ', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'idemp_conflicting_slot', 'hash_slot', 'Confirmed', NOW());

                    -- Conflicting request hash case: Pending booking has hash_conflict_b while booking has hash_conflict_a (same conversation)
                    INSERT INTO ""PendingBookings"" (""Id"", ""ConversationId"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""IdempotencyKey"", ""RequestHash"", ""Status"", ""CreatedAtUtc"")
                    VALUES ('{Guid.NewGuid()}', '{convId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'idemp_conflicting_hash', 'hash_conflict_b', 'Confirmed', NOW());

                    -- Booking 1: Unambiguous match with PendingBooking 1 -> Must be backfilled to convId1
                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_unambiguous_1', 'hash1', NOW());

                    -- Booking 2: Conflicting pending bookings with different conversation IDs -> Must NOT be backfilled arbitrarily (remains NULL)
                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bId2}', '{slotId}', 'عميل متنازع', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_conflicting_2', 'hash2', NOW());

                    -- Booking 3: Phone matches Conversation 1, but no pending booking match -> Must NOT be inferred from phone (remains NULL)
                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bId3}', '{slotId}', 'عميل أ بمطابقة هاتف فقط', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_phone_only_match', 'hash3', NOW());

                    -- Booking 4: Unresolved legacy booking with no pending matches -> Remains NULL
                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bId4}', '{slotId}', 'عميل قديم', '01033333333', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_legacy_unresolved', 'hash4', NOW());

                    -- Booking 5: Match has conflicting slot -> Must NOT be backfilled (remains NULL)
                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bIdConflictSlot}', '{slotId}', 'عميل أ بتعارض موعد', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_conflicting_slot', 'hash_slot', NOW());

                    -- Booking 6: Match has conflicting request hash -> Must NOT be backfilled (remains NULL)
                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bIdConflictHash}', '{slotId}', 'عميل أ بتعارض هاش', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_conflicting_hash', 'hash_conflict_a', NOW());
                ");
            }

            // 2. Execute safe deployment baseline and migration procedure
            using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationHelper.ApplyMigrationsAsync(db);
            }

            // 3. Verify zero data loss and strict ownership boundaries
            using (var db = new AppDbContext(options))
            {
                var bookings = await db.Bookings.AsNoTracking().ToListAsync();
                Assert.Equal(6, bookings.Count);

                var booking1 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_unambiguous_1");
                var booking2 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_conflicting_2");
                var booking3 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_phone_only_match");
                var booking4 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_legacy_unresolved");
                var bookingSlotConflict = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_conflicting_slot");
                var bookingHashConflict = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_conflicting_hash");

                Assert.NotNull(booking1);
                Assert.NotNull(booking2);
                Assert.NotNull(booking3);
                Assert.NotNull(booking4);
                Assert.NotNull(bookingSlotConflict);
                Assert.NotNull(bookingHashConflict);

                // Booking 1: Reliably backfilled from unambiguous match
                Assert.Equal(convId1, booking1.ConversationId);

                // Booking 2: Conflicting pending bookings excluded from automatic backfill
                Assert.Null(booking2.ConversationId);

                // Booking 3: Ownership MUST NOT be inferred merely from matching phone number
                Assert.Null(booking3.ConversationId);

                // Booking 4: Legacy booking with unresolved ownership remains NULL
                Assert.Null(booking4.ConversationId);

                // Booking 5 & 6: Conflicting slot or request hash records MUST NOT be backfilled
                Assert.Null(bookingSlotConflict.ConversationId);
                Assert.Null(bookingHashConflict.ConversationId);

                // Unresolved bookings retrievable by administrator (null ConversationId)
                var unresolvedAdminList = await db.Bookings.Where(b => b.ConversationId == null).ToListAsync();
                Assert.Equal(5, unresolvedAdminList.Count);

                // Customer 1 query returns strictly owned booking 1; unresolved ownership inaccessible to customer
                var customer1Query = await db.Bookings.Where(b => b.ConversationId == convId1).ToListAsync();
                Assert.Single(customer1Query);
                Assert.Equal(bId1, customer1Query[0].Id);

                // Verify capacity constraint and slot intact
                var slot = await db.AvailabilitySlots.FirstAsync(s => s.Id == slotId);
                Assert.Equal(4, slot.BookedCapacity);
                Assert.Equal(5, slot.TotalCapacity);
            }

            // 4. Test repeated migration execution: Must run idempotently with no errors
            using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationHelper.ApplyMigrationsAsync(db);
            }
        }
        finally
        {
            await DropDisposablePostgresDatabaseAsync(cs);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Postgres_PartiallyUpgradedDatabase_MissingIndexAndFk_IsRejected_AndRepairedExplicitly()
    {
        var cs = await CreateDisposablePostgresDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(cs, o => o.UseVector())
                .Options;

            var agentId = Guid.NewGuid();
            var convId = Guid.NewGuid();
            var slotId = Guid.NewGuid();
            var bId = Guid.NewGuid();

            // 1. Setup legacy schema + partially added ConversationId column (missing index, FK, and migration history)
            using (var db = new AppDbContext(options))
            {
                await CreateLegacyPostgresSchema428F490Async(db);

                // Manually add ConversationId column only (simulating incomplete manual alter)
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""Bookings"" ADD COLUMN ""ConversationId"" uuid NULL;
                ");

                await db.Database.ExecuteSqlRawAsync($@"
                    INSERT INTO ""Agents"" (""Id"", ""Name"", ""SystemPrompt"", ""ModelName"", ""LanguageCode"", ""Temperature"", ""IsActive"", ""AllowedToolsJson"", ""CreatedAtUtc"")
                    VALUES ('{agentId}', 'سارة', 'مساعد', 'qwen2.5:1.5b', 'ar-EG', 0.2, true, '[]', NOW());

                    INSERT INTO ""Conversations"" (""Id"", ""AgentId"", ""CustomerPhoneNumber"", ""CustomerName"", ""Channel"", ""Status"", ""StartedAtUtc"", ""LastActiveAtUtc"")
                    VALUES ('{convId}', '{agentId}', '01011111111', 'عميل أ', 'web', 'Active', NOW(), NOW());

                    INSERT INTO ""AvailabilitySlots"" (""Id"", ""ServiceName"", ""StartTimeUtc"", ""EndTimeUtc"", ""TotalCapacity"", ""BookedCapacity"")
                    VALUES ('{slotId}', 'كشف باطنة', NOW() + INTERVAL '1 day', NOW() + INTERVAL '1 day 30 minutes', 3, 1);

                    INSERT INTO ""PendingBookings"" (""Id"", ""ConversationId"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""IdempotencyKey"", ""RequestHash"", ""Status"", ""CreatedAtUtc"")
                    VALUES ('{Guid.NewGuid()}', '{convId}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'idemp_partial_1', 'hash1', 'Confirmed', NOW());

                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bId}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_partial_1', 'hash1', NOW());
                ");
            }

            // 2. Calling ApplyMigrationsAsync on partially upgraded schema MUST be rejected with actionable error
            using (var db = new AppDbContext(options))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrationHelper.ApplyMigrationsAsync(db));
                Assert.Contains("Partially upgraded database schema detected", ex.Message);
                Assert.Contains("RepairPartiallyUpgradedSchemaAsync", ex.Message);

                // Verify that migration history was NOT falsely written
                var hasHist = await db.Database.CanConnectAsync();
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory');";
                var histExists = (bool)(await cmd.ExecuteScalarAsync() ?? false);
                Assert.False(histExists);
            }

            // 3. Execute explicit repair path
            using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationHelper.RepairPartiallyUpgradedSchemaAsync(db);

                // Verify structural components restored
                await using var cmdIdx = db.Database.GetDbConnection().CreateCommand();
                cmdIdx.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'Bookings' AND indexname = 'IX_Bookings_ConversationId');";
                var hasIdx = (bool)(await cmdIdx.ExecuteScalarAsync() ?? false);
                Assert.True(hasIdx);

                await using var cmdFk = db.Database.GetDbConnection().CreateCommand();
                cmdFk.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE constraint_schema = 'public' AND constraint_name = 'FK_Bookings_Conversations_ConversationId');";
                var hasFk = (bool)(await cmdFk.ExecuteScalarAsync() ?? false);
                Assert.True(hasFk);

                // Verify backfill was performed
                var booking = await db.Bookings.FirstAsync(b => b.Id == bId);
                Assert.Equal(convId, booking.ConversationId);
            }

            // 4. Repeated migration execution after repair succeeds cleanly and idempotently
            using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationHelper.ApplyMigrationsAsync(db);
                var applied = await db.Database.GetAppliedMigrationsAsync();
                Assert.Contains(applied, m => m.EndsWith("_InitialCreate"));
                Assert.Contains(applied, m => m.EndsWith("_AddBookingConversationId"));
            }
        }
        finally
        {
            await DropDisposablePostgresDatabaseAsync(cs);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Postgres_CorruptedLegacySchema_MissingRequiredConstraint_IsRejected()
    {
        var cs = await CreateDisposablePostgresDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(cs, o => o.UseVector())
                .Options;

            using (var db = new AppDbContext(options))
            {
                // Create legacy schema but drop check constraint
                await CreateLegacyPostgresSchema428F490Async(db);
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""AvailabilitySlots"" DROP CONSTRAINT ""CK_AvailabilitySlots_Capacity"";
                ");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrationHelper.ApplyMigrationsAsync(db));
                Assert.Contains("Unsupported or corrupted legacy database schema", ex.Message);
                Assert.Contains("CK_AvailabilitySlots_Capacity", ex.Message);
            }
        }
        finally
        {
            await DropDisposablePostgresDatabaseAsync(cs);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Postgres_LegacySchema_NonUniqueIdempotencyIndex_IsRejected()
    {
        var cs = await CreateDisposablePostgresDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(cs, o => o.UseVector())
                .Options;

            using (var db = new AppDbContext(options))
            {
                await CreateLegacyPostgresSchema428F490Async(db);
                // Replace unique index with a non-unique index using the expected index name
                await db.Database.ExecuteSqlRawAsync(@"
                    DROP INDEX ""IX_Bookings_IdempotencyKey"";
                    CREATE INDEX ""IX_Bookings_IdempotencyKey"" ON ""Bookings"" (""IdempotencyKey"");
                ");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrationHelper.ApplyMigrationsAsync(db));
                Assert.Contains("IX_Bookings_IdempotencyKey", ex.Message);
                Assert.Contains("UNIQUE", ex.Message);

                // Verify migration history was not falsely updated
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory');";
                var histExists = (bool)(await cmd.ExecuteScalarAsync() ?? false);
                Assert.False(histExists);
            }
        }
        finally
        {
            await DropDisposablePostgresDatabaseAsync(cs);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Postgres_LegacySchema_IneffectiveCapacityConstraint_IsRejected()
    {
        var cs = await CreateDisposablePostgresDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(cs, o => o.UseVector())
                .Options;

            using (var db = new AppDbContext(options))
            {
                await CreateLegacyPostgresSchema428F490Async(db);
                // Replace capacity constraint with an ineffective check constraint using the expected constraint name
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""AvailabilitySlots"" DROP CONSTRAINT ""CK_AvailabilitySlots_Capacity"";
                    ALTER TABLE ""AvailabilitySlots"" ADD CONSTRAINT ""CK_AvailabilitySlots_Capacity"" CHECK (true);
                ");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrationHelper.ApplyMigrationsAsync(db));
                Assert.Contains("CK_AvailabilitySlots_Capacity", ex.Message);
                Assert.Contains("does not enforce BookedCapacity <= TotalCapacity", ex.Message);

                // Verify migration history was not falsely updated
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory');";
                var histExists = (bool)(await cmd.ExecuteScalarAsync() ?? false);
                Assert.False(histExists);
            }
        }
        finally
        {
            await DropDisposablePostgresDatabaseAsync(cs);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Postgres_MissingBookingOwnershipFkOrIndex_WhileMigrationHistoryPresent_IsRejected()
    {
        var cs = await CreateDisposablePostgresDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(cs, o => o.UseVector())
                .Options;

            using (var db = new AppDbContext(options))
            {
                // Initialize clean database with migrations applied
                await DatabaseMigrationHelper.ApplyMigrationsAsync(db);

                // Simulate corrupted state: migration history is present, ConversationId column exists,
                // but the foreign key constraint is dropped
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""Bookings"" DROP CONSTRAINT ""FK_Bookings_Conversations_ConversationId"";
                ");

                // Calling ApplyMigrationsAsync must detect missing FK even though AddBookingConversationId is in history
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrationHelper.ApplyMigrationsAsync(db));
                Assert.Contains("Partially upgraded", ex.Message);
                Assert.Contains("FK: False", ex.Message);
                Assert.Contains("RepairPartiallyUpgradedSchemaAsync", ex.Message);
            }
        }
        finally
        {
            await DropDisposablePostgresDatabaseAsync(cs);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Postgres_LegacySchema_MissingRequiredColumn_IsRejected()
    {
        var cs = await CreateDisposablePostgresDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(cs, o => o.UseVector())
                .Options;

            using (var db = new AppDbContext(options))
            {
                await CreateLegacyPostgresSchema428F490Async(db);
                // Drop a required application column from Bookings
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""Bookings"" DROP COLUMN ""RequestHash"";
                ");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrationHelper.ApplyMigrationsAsync(db));
                Assert.Contains("Missing required application column 'RequestHash'", ex.Message);

                // Verify migration history was not falsely updated
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory');";
                var histExists = (bool)(await cmd.ExecuteScalarAsync() ?? false);
                Assert.False(histExists);
            }
        }
        finally
        {
            await DropDisposablePostgresDatabaseAsync(cs);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Postgres_RepairOperation_FailsAndRollsBack_OnValidationFailure()
    {
        var cs = await CreateDisposablePostgresDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(cs, o => o.UseVector())
                .Options;

            var slotId = Guid.NewGuid();

            using (var db = new AppDbContext(options))
            {
                await CreateLegacyPostgresSchema428F490Async(db);
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""Bookings"" ADD COLUMN ""ConversationId"" uuid NULL;
                ");

                // Insert a slot that violates business capacity constraint
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""AvailabilitySlots"" DROP CONSTRAINT ""CK_AvailabilitySlots_Capacity"";
                ");
                await db.Database.ExecuteSqlRawAsync($@"
                    INSERT INTO ""AvailabilitySlots"" (""Id"", ""ServiceName"", ""StartTimeUtc"", ""EndTimeUtc"", ""TotalCapacity"", ""BookedCapacity"")
                    VALUES ('{slotId}', 'كشف باطنة', NOW(), NOW() + INTERVAL '30 minutes', 2, 5);
                ");
                // Re-add dummy check constraint to pass initial legacy check but fail capacity check step in repair
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""AvailabilitySlots"" ADD CONSTRAINT ""CK_AvailabilitySlots_Capacity"" CHECK (""BookedCapacity"" <= ""TotalCapacity"") NOT VALID;
                ");

                // Execute Repair: must throw capacity violation and roll back transaction
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrationHelper.RepairPartiallyUpgradedSchemaAsync(db));
                Assert.Contains("Capacity constraint violation", ex.Message);

                // Verify rollback: __EFMigrationsHistory must not exist and AddBookingConversationId must not be recorded
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory');";
                var histExists = (bool)(await cmd.ExecuteScalarAsync() ?? false);
                Assert.False(histExists);
            }
        }
        finally
        {
            await DropDisposablePostgresDatabaseAsync(cs);
        }
    }

    [Fact]
    public async Task Sqlite_UpgradePopulatedLegacyDatabase_EnsureCreated_PreservesData_AndBackfillsReliably()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sqlite_mig_test_{Guid.NewGuid():N}.db");
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

            // 1. Create legacy schema via raw SQLite DDL matching EnsureCreated on previous revision
            using (var db = new AppDbContext(options))
            {
                await db.Database.OpenConnectionAsync();
                await db.Database.ExecuteSqlRawAsync($@"
                    CREATE TABLE Agents (
                        Id TEXT NOT NULL PRIMARY KEY,
                        Name TEXT NOT NULL,
                        SystemPrompt TEXT NOT NULL,
                        ModelName TEXT NOT NULL,
                        LanguageCode TEXT NOT NULL,
                        Temperature REAL NOT NULL,
                        IsActive INTEGER NOT NULL,
                        AllowedToolsJson TEXT NOT NULL,
                        CreatedAtUtc TEXT NOT NULL
                    );

                    CREATE TABLE AvailabilitySlots (
                        Id TEXT NOT NULL PRIMARY KEY,
                        ServiceName TEXT NOT NULL,
                        StartTimeUtc TEXT NOT NULL,
                        EndTimeUtc TEXT NOT NULL,
                        TotalCapacity INTEGER NOT NULL,
                        BookedCapacity INTEGER NOT NULL
                    );

                    CREATE TABLE Conversations (
                        Id TEXT NOT NULL PRIMARY KEY,
                        AgentId TEXT NOT NULL REFERENCES Agents(Id),
                        CustomerPhoneNumber TEXT,
                        CustomerName TEXT,
                        Channel TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        StartedAtUtc TEXT NOT NULL,
                        LastActiveAtUtc TEXT NOT NULL
                    );

                    CREATE TABLE Messages (
                        Id TEXT NOT NULL PRIMARY KEY,
                        ConversationId TEXT NOT NULL REFERENCES Conversations(Id),
                        Role TEXT NOT NULL,
                        Content TEXT NOT NULL,
                        SequenceNumber INTEGER NOT NULL,
                        CreatedAtUtc TEXT NOT NULL
                    );

                    CREATE TABLE ToolExecutions (
                        Id TEXT NOT NULL PRIMARY KEY,
                        ConversationId TEXT NOT NULL REFERENCES Conversations(Id),
                        ToolName TEXT NOT NULL,
                        ArgumentsJson TEXT NOT NULL,
                        ResultJson TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        DurationMs INTEGER NOT NULL,
                        ExecutedAtUtc TEXT NOT NULL
                    );

                    CREATE TABLE PendingBookings (
                        Id TEXT NOT NULL PRIMARY KEY,
                        ConversationId TEXT NOT NULL REFERENCES Conversations(Id),
                        SlotId TEXT NOT NULL REFERENCES AvailabilitySlots(Id),
                        CustomerName TEXT NOT NULL,
                        CustomerPhone TEXT NOT NULL,
                        ServiceName TEXT NOT NULL,
                        BookingDateUtc TEXT NOT NULL,
                        IdempotencyKey TEXT NOT NULL,
                        RequestHash TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        CreatedAtUtc TEXT NOT NULL,
                        ConfirmedAtUtc TEXT
                    );

                    CREATE TABLE Bookings (
                        Id TEXT NOT NULL PRIMARY KEY,
                        SlotId TEXT NOT NULL REFERENCES AvailabilitySlots(Id),
                        CustomerName TEXT NOT NULL,
                        CustomerPhone TEXT NOT NULL,
                        ServiceName TEXT NOT NULL,
                        BookingDateUtc TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        IdempotencyKey TEXT NOT NULL UNIQUE,
                        RequestHash TEXT NOT NULL,
                        CreatedAtUtc TEXT NOT NULL
                    );

                    CREATE TABLE KnowledgeDocuments (
                        Id TEXT NOT NULL PRIMARY KEY,
                        Title TEXT NOT NULL,
                        FileName TEXT NOT NULL,
                        Category TEXT NOT NULL,
                        ChunkCount INTEGER NOT NULL,
                        CreatedAtUtc TEXT NOT NULL
                    );

                    CREATE TABLE DocumentChunks (
                        Id TEXT NOT NULL PRIMARY KEY,
                        DocumentId TEXT NOT NULL REFERENCES KnowledgeDocuments(Id),
                        ChunkIndex INTEGER NOT NULL,
                        Content TEXT NOT NULL,
                        CreatedAtUtc TEXT NOT NULL
                    );

                    CREATE TABLE OutboxJobs (
                        Id TEXT NOT NULL PRIMARY KEY,
                        Topic TEXT NOT NULL,
                        PayloadJson TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        RetryCount INTEGER NOT NULL,
                        MaxRetries INTEGER NOT NULL,
                        NextRetryUtc TEXT NOT NULL,
                        CreatedAtUtc TEXT NOT NULL,
                        ProcessedAtUtc TEXT,
                        LastError TEXT
                    );

                    INSERT INTO Agents VALUES ('{agentId}', 'سارة', 'مساعد', 'qwen2.5:1.5b', 'ar-EG', 0.2, 1, '[]', '{DateTime.UtcNow:O}');
                    INSERT INTO Conversations VALUES ('{convId1}', '{agentId}', '01011111111', 'عميل أ', 'web', 'Active', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}');
                    INSERT INTO Conversations VALUES ('{convId2}', '{agentId}', '01022222222', 'عميل ب', 'web', 'Active', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}');
                    INSERT INTO AvailabilitySlots VALUES ('{slotId}', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', '{DateTime.UtcNow.AddDays(1).AddMinutes(30):O}', 3, 3);
                    
                    -- Unambiguous pending booking
                    INSERT INTO PendingBookings VALUES ('{Guid.NewGuid()}', '{convId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'idemp_sqlite_1', 'hash1', 'Confirmed', '{DateTime.UtcNow:O}', NULL);
                    
                    -- Conflicting pending bookings with different conversation IDs
                    INSERT INTO PendingBookings VALUES ('{Guid.NewGuid()}', '{convId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'idemp_sqlite_conflict', 'hash2a', 'Confirmed', '{DateTime.UtcNow:O}', NULL);
                    INSERT INTO PendingBookings VALUES ('{Guid.NewGuid()}', '{convId2}', '{slotId}', 'عميل ب', '01022222222', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'idemp_sqlite_conflict', 'hash2b', 'Confirmed', '{DateTime.UtcNow:O}', NULL);

                    INSERT INTO Bookings VALUES ('{bId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'Confirmed', 'idemp_sqlite_1', 'hash1', '{DateTime.UtcNow:O}');
                    INSERT INTO Bookings VALUES ('{bId2}', '{slotId}', 'عميل متنازع', '01011111111', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'Confirmed', 'idemp_sqlite_conflict', 'hash2', '{DateTime.UtcNow:O}');
                    INSERT INTO Bookings VALUES ('{bId3}', '{slotId}', 'عميل قديم', '01033333333', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'Confirmed', 'idemp_sqlite_legacy', 'hash3', '{DateTime.UtcNow:O}');
                ");
            }

            // 2. Run safe baseline & migration helper
            using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationHelper.ApplyMigrationsAsync(db);
            }

            // 3. Verify data preservation and unambiguous backfill
            using (var db = new AppDbContext(options))
            {
                var bookings = await db.Bookings.AsNoTracking().ToListAsync();
                Assert.Equal(3, bookings.Count);

                var booking1 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_sqlite_1");
                var bookingConflict = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_sqlite_conflict");
                var bookingLegacy = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_sqlite_legacy");

                Assert.NotNull(booking1);
                Assert.NotNull(bookingConflict);
                Assert.NotNull(bookingLegacy);

                Assert.Equal(convId1, booking1.ConversationId);
                Assert.Null(bookingConflict.ConversationId);
                Assert.Null(bookingLegacy.ConversationId);
            }

            // 4. Repeated execution runs cleanly
            using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationHelper.ApplyMigrationsAsync(db);
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
