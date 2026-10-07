using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Infrastructure.Persistence;

/// <summary>
/// Safe database migration helper for production and development deployments.
/// Replaces unversioned EnsureCreatedAsync with safe, reversible EF Core migrations.
/// Provides trustworthy baselining with full schema integrity validation, rejection of corrupted/partially
/// upgraded databases, unambiguous relationship backfilling, and explicit repair pathways.
/// </summary>
public static class DatabaseMigrationHelper
{
    private static readonly string[] RequiredLegacyTables =
    [
        "Agents",
        "Conversations",
        "Messages",
        "ToolExecutions",
        "AvailabilitySlots",
        "PendingBookings",
        "Bookings",
        "KnowledgeDocuments",
        "DocumentChunks",
        "OutboxJobs"
    ];

    public static async Task ApplyMigrationsAsync(AppDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var isNpgsql = db.Database.IsNpgsql();
        var isSqlite = db.Database.IsSqlite();

        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        bool hasMigrationHistory = false;
        bool hasLegacyTables = false;
        bool hasBookingConversationId = false;
        bool hasBookingConversationIndex = false;
        bool hasBookingConversationFk = false;

        if (isNpgsql)
        {
            await using var cmdHist = db.Database.GetDbConnection().CreateCommand();
            cmdHist.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory');";
            hasMigrationHistory = (bool)(await cmdHist.ExecuteScalarAsync(ct) ?? false);

            await using var cmdBookings = db.Database.GetDbConnection().CreateCommand();
            cmdBookings.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Bookings');";
            hasLegacyTables = (bool)(await cmdBookings.ExecuteScalarAsync(ct) ?? false);

            if (hasLegacyTables)
            {
                await using var cmdCol = db.Database.GetDbConnection().CreateCommand();
                cmdCol.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND lower(table_name) = 'bookings' AND lower(column_name) = 'conversationid');";
                hasBookingConversationId = (bool)(await cmdCol.ExecuteScalarAsync(ct) ?? false);

                await using var cmdIdx = db.Database.GetDbConnection().CreateCommand();
                cmdIdx.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'Bookings' AND indexname = 'IX_Bookings_ConversationId');";
                hasBookingConversationIndex = (bool)(await cmdIdx.ExecuteScalarAsync(ct) ?? false);

                await using var cmdFk = db.Database.GetDbConnection().CreateCommand();
                cmdFk.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE constraint_schema = 'public' AND constraint_name = 'FK_Bookings_Conversations_ConversationId');";
                hasBookingConversationFk = (bool)(await cmdFk.ExecuteScalarAsync(ct) ?? false);
            }
        }
        else if (isSqlite)
        {
            await using var cmdHist = db.Database.GetDbConnection().CreateCommand();
            cmdHist.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
            var histCount = Convert.ToInt64(await cmdHist.ExecuteScalarAsync(ct) ?? 0);
            hasMigrationHistory = histCount > 0;

            await using var cmdBookings = db.Database.GetDbConnection().CreateCommand();
            cmdBookings.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='Bookings';";
            var bookingsCount = Convert.ToInt64(await cmdBookings.ExecuteScalarAsync(ct) ?? 0);
            hasLegacyTables = bookingsCount > 0;

            if (hasLegacyTables)
            {
                await using var cmdCol = db.Database.GetDbConnection().CreateCommand();
                cmdCol.CommandText = "SELECT COUNT(1) FROM pragma_table_info('Bookings') WHERE lower(name) = 'conversationid';";
                var colCount = Convert.ToInt64(await cmdCol.ExecuteScalarAsync(ct) ?? 0);
                hasBookingConversationId = colCount > 0;
            }
        }

        // Check if AddBookingConversationId is already recorded in __EFMigrationsHistory
        bool hasAddBookingConversationIdInHistory = false;
        if (hasMigrationHistory)
        {
            await using var cmdMig = db.Database.GetDbConnection().CreateCommand();
            cmdMig.CommandText = isNpgsql
                ? "SELECT EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%AddBookingConversationId');"
                : "SELECT COUNT(1) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%AddBookingConversationId';";
            var res = await cmdMig.ExecuteScalarAsync(ct);
            hasAddBookingConversationIdInHistory = isNpgsql ? (bool)(res ?? false) : Convert.ToInt64(res ?? 0) > 0;
        }

        // Check if database has legacy data without migration history or is partially upgraded
        if (hasLegacyTables)
        {
            // Case 1: Partially upgraded database (ConversationId column exists but missing index/FK or migration history)
            if (hasBookingConversationId && !hasAddBookingConversationIdInHistory)
            {
                // Reject without falsely marking migrations applied
                throw new InvalidOperationException(
                    "Partially upgraded database schema detected: 'Bookings' table contains 'ConversationId' column, " +
                    $"but required structural components are incomplete (Index: {hasBookingConversationIndex}, FK: {hasBookingConversationFk}, MigrationHistory: {hasAddBookingConversationIdInHistory}). " +
                    "Baselining cannot proceed automatically without risk of unapplied schema integrity. " +
                    "To complete the upgrade safely and backfill ownership, invoke DatabaseMigrationHelper.RepairPartiallyUpgradedSchemaAsync(db).");
            }

            // Case 2: Legacy database matching previous main revision without migration history
            if (!hasMigrationHistory)
            {
                // Must validate full supported schema before recording InitialCreate
                await ValidateSupportedLegacySchemaAsync(db, logger, ct);

                var migrations = db.Database.GetMigrations().ToList();
                var initialCreateId = migrations.FirstOrDefault(m => m.EndsWith("_InitialCreate"));

                if (!string.IsNullOrEmpty(initialCreateId))
                {
                    logger?.LogInformation("Baselining InitialCreate into __EFMigrationsHistory for validated legacy schema...");
                    if (isNpgsql)
                    {
                        await db.Database.ExecuteSqlAsync($@"
                            CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                                ""MigrationId"" character varying(150) NOT NULL,
                                ""ProductVersion"" character varying(32) NOT NULL,
                                CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                            );
                            INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                            VALUES ({initialCreateId}, '10.0.12')
                            ON CONFLICT (""MigrationId"") DO NOTHING;
                        ", ct);
                    }
                    else if (isSqlite)
                    {
                        await db.Database.ExecuteSqlAsync($@"
                            CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                                ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                                ""ProductVersion"" TEXT NOT NULL
                            );
                            INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                            VALUES ({initialCreateId}, '10.0.12');
                        ", ct);
                    }
                    logger?.LogInformation("Successfully baselined InitialCreate ({MigrationId})", initialCreateId);
                }
            }
        }

        logger?.LogInformation("Applying EF Core database migrations...");
        await db.Database.MigrateAsync(ct);
        logger?.LogInformation("EF Core database migrations applied successfully.");
    }

    /// <summary>
    /// Validates that an unversioned database conforms strictly to the supported schema from commit 428f490.
    /// Rejects unsupported or partially upgraded schemas with an actionable error.
    /// </summary>
    public static async Task ValidateSupportedLegacySchemaAsync(AppDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var isNpgsql = db.Database.IsNpgsql();
        var isSqlite = db.Database.IsSqlite();

        var missingErrors = new List<string>();

        if (isNpgsql)
        {
            // 1. Validate required tables
            await using var cmdTables = db.Database.GetDbConnection().CreateCommand();
            cmdTables.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';";
            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await cmdTables.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    existingTables.Add(reader.GetString(0));
                }
            }

            foreach (var tbl in RequiredLegacyTables)
            {
                if (!existingTables.Contains(tbl))
                {
                    missingErrors.Add($"Missing required table: {tbl}");
                }
            }

            // 2. Validate pgvector extension
            await using var cmdVec = db.Database.GetDbConnection().CreateCommand();
            cmdVec.CommandText = "SELECT COUNT(1) FROM pg_extension WHERE extname = 'vector';";
            var vecCount = Convert.ToInt64(await cmdVec.ExecuteScalarAsync(ct) ?? 0);
            if (vecCount == 0)
            {
                missingErrors.Add("Missing required pgvector extension");
            }

            // 3. Validate capacity check constraint on AvailabilitySlots
            await using var cmdCheck = db.Database.GetDbConnection().CreateCommand();
            cmdCheck.CommandText = "SELECT COUNT(1) FROM information_schema.check_constraints WHERE constraint_name = 'CK_AvailabilitySlots_Capacity';";
            var checkCount = Convert.ToInt64(await cmdCheck.ExecuteScalarAsync(ct) ?? 0);
            if (checkCount == 0)
            {
                missingErrors.Add("Missing capacity check constraint: CK_AvailabilitySlots_Capacity");
            }

            // 4. Validate unique index on Bookings.IdempotencyKey
            await using var cmdIdx = db.Database.GetDbConnection().CreateCommand();
            cmdIdx.CommandText = "SELECT COUNT(1) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'Bookings' AND indexname = 'IX_Bookings_IdempotencyKey';";
            var idxCount = Convert.ToInt64(await cmdIdx.ExecuteScalarAsync(ct) ?? 0);
            if (idxCount == 0)
            {
                missingErrors.Add("Missing required unique index: IX_Bookings_IdempotencyKey");
            }

            // 5. Validate key column types
            await using var cmdCols = db.Database.GetDbConnection().CreateCommand();
            cmdCols.CommandText = "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Bookings';";
            var colTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await cmdCols.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    colTypes[reader.GetString(0)] = reader.GetString(1);
                }
            }

            if (!colTypes.TryGetValue("Id", out var idType) || !idType.Equals("uuid", StringComparison.OrdinalIgnoreCase))
            {
                missingErrors.Add($"Invalid data type for Bookings.Id: expected 'uuid', got '{idType}'");
            }
            if (!colTypes.TryGetValue("IdempotencyKey", out var idempType) || !idempType.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                missingErrors.Add($"Invalid data type for Bookings.IdempotencyKey: expected 'text', got '{idempType}'");
            }
        }
        else if (isSqlite)
        {
            await using var cmdTables = db.Database.GetDbConnection().CreateCommand();
            cmdTables.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await cmdTables.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    existingTables.Add(reader.GetString(0));
                }
            }

            foreach (var tbl in RequiredLegacyTables)
            {
                if (!existingTables.Contains(tbl))
                {
                    missingErrors.Add($"Missing required table: {tbl}");
                }
            }
        }

        if (missingErrors.Count > 0)
        {
            var details = string.Join("; ", missingErrors);
            logger?.LogError("Unsupported legacy database schema rejected: {Details}", details);
            throw new InvalidOperationException(
                $"Unsupported or corrupted legacy database schema: {details}. " +
                "The database cannot be safely baselined. Please ensure schema conforms to commit 428f490 or use a fresh database.");
        }
    }

    /// <summary>
    /// Explicit repair path for partially upgraded databases containing ConversationId but missing
    /// index, foreign key, migration history, or backfill.
    /// Preserves existing records, restores relationships, enforces capacity constraints,
    /// and records migrations reliably so subsequent migrations succeed idempotently.
    /// </summary>
    public static async Task RepairPartiallyUpgradedSchemaAsync(AppDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var isNpgsql = db.Database.IsNpgsql();
        var isSqlite = db.Database.IsSqlite();

        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        logger?.LogInformation("Executing explicit repair for partially upgraded schema...");

        // 1. Validate supported legacy foundation
        await ValidateSupportedLegacySchemaAsync(db, logger, ct);

        // 2. Structural repair
        if (isNpgsql)
        {
            // Ensure column exists
            await db.Database.ExecuteSqlRawAsync(@"
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""ConversationId"" uuid NULL;
            ", ct);

            // Ensure index exists
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE INDEX IF NOT EXISTS ""IX_Bookings_ConversationId"" ON ""Bookings"" (""ConversationId"");
            ", ct);

            // Ensure foreign key constraint exists
            await db.Database.ExecuteSqlRawAsync(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.table_constraints
                        WHERE constraint_schema = 'public' AND constraint_name = 'FK_Bookings_Conversations_ConversationId'
                    ) THEN
                        ALTER TABLE ""Bookings""
                        ADD CONSTRAINT ""FK_Bookings_Conversations_ConversationId""
                        FOREIGN KEY (""ConversationId"") REFERENCES ""Conversations"" (""Id"") ON DELETE SET NULL;
                    END IF;
                END $$;
            ", ct);

            // 3. Unambiguous ownership backfill:
            // Backfill ownership exclusively from unambiguous, reliable relationships (IdempotencyKey matching PendingBooking).
            // Ambiguous/conflicting records (multiple PendingBookings with differing ConversationIds) MUST NOT be backfilled.
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE ""Bookings"" b
                SET ""ConversationId"" = u.""ConversationId""
                FROM (
                    SELECT pb.""IdempotencyKey"", MIN(pb.""ConversationId""::text)::uuid AS ""ConversationId""
                    FROM ""PendingBookings"" pb
                    WHERE pb.""ConversationId"" IS NOT NULL
                    GROUP BY pb.""IdempotencyKey""
                    HAVING COUNT(DISTINCT pb.""ConversationId"") = 1
                ) u
                WHERE b.""IdempotencyKey"" = u.""IdempotencyKey""
                  AND b.""ConversationId"" IS NULL;
            ", ct);

            // 4. Verify capacity constraints are intact
            await using var cmdCap = db.Database.GetDbConnection().CreateCommand();
            cmdCap.CommandText = "SELECT COUNT(1) FROM \"AvailabilitySlots\" WHERE \"BookedCapacity\" > \"TotalCapacity\";";
            var capViolations = Convert.ToInt64(await cmdCap.ExecuteScalarAsync(ct) ?? 0);
            if (capViolations > 0)
            {
                throw new InvalidOperationException($"Capacity constraint violation: {capViolations} slots exceed TotalCapacity.");
            }

            // 5. Record both migrations in __EFMigrationsHistory
            var migrations = db.Database.GetMigrations().ToList();
            var initialCreateId = migrations.FirstOrDefault(m => m.EndsWith("_InitialCreate")) ?? "20261006213907_InitialCreate";
            var addBookingConvId = migrations.FirstOrDefault(m => m.EndsWith("_AddBookingConversationId")) ?? "20261006214001_AddBookingConversationId";

            await db.Database.ExecuteSqlAsync($@"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" character varying(150) NOT NULL,
                    ""ProductVersion"" character varying(32) NOT NULL,
                    CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                );
                INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ({initialCreateId}, '10.0.12')
                ON CONFLICT (""MigrationId"") DO NOTHING;
                INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ({addBookingConvId}, '10.0.12')
                ON CONFLICT (""MigrationId"") DO NOTHING;
            ", ct);
        }
        else if (isSqlite)
        {
            // For SQLite, ensure column exists
            try
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Bookings ADD COLUMN ConversationId TEXT NULL;");
            }
            catch { }

            // Ensure index exists
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Bookings_ConversationId ON Bookings (ConversationId);");

            // Backfill unambiguously
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE Bookings
                SET ConversationId = (
                    SELECT pb.ConversationId
                    FROM PendingBookings pb
                    WHERE pb.IdempotencyKey = Bookings.IdempotencyKey
                      AND pb.ConversationId IS NOT NULL
                    GROUP BY pb.IdempotencyKey
                    HAVING COUNT(DISTINCT pb.ConversationId) = 1
                )
                WHERE ConversationId IS NULL
                  AND IdempotencyKey IN (
                    SELECT pb.IdempotencyKey
                    FROM PendingBookings pb
                    WHERE pb.ConversationId IS NOT NULL
                    GROUP BY pb.IdempotencyKey
                    HAVING COUNT(DISTINCT pb.ConversationId) = 1
                  );
            ");

            var migrations = db.Database.GetMigrations().ToList();
            var initialCreateId = migrations.FirstOrDefault(m => m.EndsWith("_InitialCreate")) ?? "20261006213907_InitialCreate";
            var addBookingConvId = migrations.FirstOrDefault(m => m.EndsWith("_AddBookingConversationId")) ?? "20261006214001_AddBookingConversationId";

            await db.Database.ExecuteSqlAsync($@"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                    ""ProductVersion"" TEXT NOT NULL
                );
                INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ({initialCreateId}, '10.0.12');
                INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ({addBookingConvId}, '10.0.12');
            ", ct);
        }

        logger?.LogInformation("Partially upgraded schema repaired and migration history synchronized successfully.");
    }
}
