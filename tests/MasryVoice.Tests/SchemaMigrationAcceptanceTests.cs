using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Infrastructure.Persistence;
using Xunit;

namespace MasryVoice.Tests;

/// <summary>
/// Acceptance tests validating provider-correct schema migrations and baseline handling
/// across both PostgreSQL and SQLite. Verifies fresh initialization, safe baselining of
/// unversioned EnsureCreated databases matching the previous main revision, zero customer
/// data loss, and reliable ownership backfilling.
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
            var bId1 = Guid.NewGuid();
            var bId2 = Guid.NewGuid();
            var bId3 = Guid.NewGuid();

            // 1. Setup legacy schema matching previous main revision (InitialCreate schema without ConversationId)
            using (var db = new AppDbContext(options))
            {
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
                var migrations = db.Database.GetMigrations().ToList();
                var initialCreateName = migrations.First(m => m.EndsWith("_InitialCreate"));
                await migrator.MigrateAsync(initialCreateName);

                // Simulate EnsureCreated database: drop __EFMigrationsHistory so there is no migration history
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsHistory\";");

                // Populate legacy data
                await db.Database.ExecuteSqlRawAsync($@"
                    INSERT INTO ""Agents"" (""Id"", ""Name"", ""SystemPrompt"", ""ModelName"", ""LanguageCode"", ""Temperature"", ""IsActive"", ""AllowedToolsJson"", ""CreatedAtUtc"")
                    VALUES ('{agentId}', 'سارة', 'مساعد', 'qwen2.5:1.5b', 'ar-EG', 0.2, true, '[]', NOW());

                    INSERT INTO ""Conversations"" (""Id"", ""AgentId"", ""CustomerPhoneNumber"", ""CustomerName"", ""Channel"", ""Status"", ""StartedAtUtc"", ""LastActiveAtUtc"")
                    VALUES ('{convId1}', '{agentId}', '01011111111', 'عميل أ', 'web', 'Active', NOW(), NOW());

                    INSERT INTO ""Conversations"" (""Id"", ""AgentId"", ""CustomerPhoneNumber"", ""CustomerName"", ""Channel"", ""Status"", ""StartedAtUtc"", ""LastActiveAtUtc"")
                    VALUES ('{convId2}', '{agentId}', '01022222222', 'عميل ب', 'web', 'Active', NOW(), NOW());

                    INSERT INTO ""AvailabilitySlots"" (""Id"", ""ServiceName"", ""StartTimeUtc"", ""EndTimeUtc"", ""TotalCapacity"", ""BookedCapacity"")
                    VALUES ('{slotId}', 'كشف باطنة', NOW() + INTERVAL '1 day', NOW() + INTERVAL '1 day 30 minutes', 3, 3);

                    -- Pending booking for Conversation 1
                    INSERT INTO ""PendingBookings"" (""Id"", ""ConversationId"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""IdempotencyKey"", ""RequestHash"", ""Status"", ""CreatedAtUtc"")
                    VALUES ('{Guid.NewGuid()}', '{convId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'idemp_conv1_slot1', 'hash1', 'Confirmed', NOW());

                    -- Booking 1: Reliable match with PendingBooking 1 via IdempotencyKey
                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_conv1_slot1', 'hash1', NOW());

                    -- Booking 2: Phone matches Conversation 1, but IdempotencyKey does NOT match any pending booking.
                    -- Ownership MUST NOT be inferred from phone number!
                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bId2}', '{slotId}', 'عميل أ مختلف', '01011111111', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_unrelated_phone_match', 'hash2', NOW());

                    -- Booking 3: Legacy booking with unresolved ownership
                    INSERT INTO ""Bookings"" (""Id"", ""SlotId"", ""CustomerName"", ""CustomerPhone"", ""ServiceName"", ""BookingDateUtc"", ""Status"", ""IdempotencyKey"", ""RequestHash"", ""CreatedAtUtc"")
                    VALUES ('{bId3}', '{slotId}', 'عميل قديم', '01033333333', 'كشف باطنة', NOW() + INTERVAL '1 day', 'Confirmed', 'idemp_legacy_unresolved', 'hash3', NOW());
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

                // Legacy unresolved bookings remain retrievable by administrator (null ConversationId)
                var unresolvedAdminList = await db.Bookings.Where(b => b.ConversationId == null).ToListAsync();
                Assert.Equal(2, unresolvedAdminList.Count);

                // But customer A cannot claim unresolved legacy bookings (strict conversation isolation)
                var customerAQuery = await db.Bookings.Where(b => b.ConversationId == convId1).ToListAsync();
                Assert.Single(customerAQuery);
                Assert.Equal(bId1, customerAQuery[0].Id);
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
            var slotId = Guid.NewGuid();
            var bId1 = Guid.NewGuid();
            var bId2 = Guid.NewGuid();

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

                    INSERT INTO Agents VALUES ('{agentId}', 'سارة', 'مساعد', 'qwen2.5:1.5b', 'ar-EG', 0.2, 1, '[]', '{DateTime.UtcNow:O}');
                    INSERT INTO Conversations VALUES ('{convId1}', '{agentId}', '01011111111', 'عميل أ', 'web', 'Active', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}');
                    INSERT INTO AvailabilitySlots VALUES ('{slotId}', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', '{DateTime.UtcNow.AddDays(1).AddMinutes(30):O}', 2, 2);
                    INSERT INTO PendingBookings VALUES ('{Guid.NewGuid()}', '{convId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'idemp_sqlite_1', 'hash1', 'Confirmed', '{DateTime.UtcNow:O}', NULL);
                    INSERT INTO Bookings VALUES ('{bId1}', '{slotId}', 'عميل أ', '01011111111', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'Confirmed', 'idemp_sqlite_1', 'hash1', '{DateTime.UtcNow:O}');
                    INSERT INTO Bookings VALUES ('{bId2}', '{slotId}', 'عميل قديم', '01022222222', 'كشف باطنة', '{DateTime.UtcNow.AddDays(1):O}', 'Confirmed', 'idemp_sqlite_legacy', 'hash2', '{DateTime.UtcNow:O}');
                ");
            }

            // 2. Run safe baseline & migration helper
            using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationHelper.ApplyMigrationsAsync(db);
            }

            // 3. Verify data preservation and backfill
            using (var db = new AppDbContext(options))
            {
                var bookings = await db.Bookings.AsNoTracking().ToListAsync();
                Assert.Equal(2, bookings.Count);

                var booking1 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_sqlite_1");
                var booking2 = bookings.FirstOrDefault(b => b.IdempotencyKey == "idemp_sqlite_legacy");

                Assert.NotNull(booking1);
                Assert.NotNull(booking2);
                Assert.Equal(convId1, booking1.ConversationId);
                Assert.Null(booking2.ConversationId);
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
