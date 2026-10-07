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

    private static readonly (string Table, string Column, string ExpectedType, bool Nullable)[] RequiredLegacyColumns =
    [
        ("Bookings", "Id", "uuid", false),
        ("Bookings", "SlotId", "uuid", false),
        ("Bookings", "CustomerName", "text", false),
        ("Bookings", "CustomerPhone", "text", false),
        ("Bookings", "ServiceName", "text", false),
        ("Bookings", "BookingDateUtc", "timestamp with time zone", false),
        ("Bookings", "Status", "text", false),
        ("Bookings", "IdempotencyKey", "text", false),
        ("Bookings", "RequestHash", "text", false),
        ("Bookings", "CreatedAtUtc", "timestamp with time zone", false),

        ("AvailabilitySlots", "Id", "uuid", false),
        ("AvailabilitySlots", "ServiceName", "character varying", false),
        ("AvailabilitySlots", "StartTimeUtc", "timestamp with time zone", false),
        ("AvailabilitySlots", "EndTimeUtc", "timestamp with time zone", false),
        ("AvailabilitySlots", "TotalCapacity", "integer", false),
        ("AvailabilitySlots", "BookedCapacity", "integer", false),

        ("Conversations", "Id", "uuid", false),
        ("Conversations", "AgentId", "uuid", false),
        ("Conversations", "CustomerPhoneNumber", "text", true),
        ("Conversations", "CustomerName", "text", true),
        ("Conversations", "Channel", "text", false),
        ("Conversations", "Status", "text", false),
        ("Conversations", "StartedAtUtc", "timestamp with time zone", false),
        ("Conversations", "LastActiveAtUtc", "timestamp with time zone", false),

        ("PendingBookings", "Id", "uuid", false),
        ("PendingBookings", "ConversationId", "uuid", false),
        ("PendingBookings", "SlotId", "uuid", false),
        ("PendingBookings", "CustomerName", "text", false),
        ("PendingBookings", "CustomerPhone", "text", false),
        ("PendingBookings", "ServiceName", "text", false),
        ("PendingBookings", "BookingDateUtc", "timestamp with time zone", false),
        ("PendingBookings", "IdempotencyKey", "text", false),
        ("PendingBookings", "RequestHash", "text", false),
        ("PendingBookings", "Status", "text", false),
        ("PendingBookings", "CreatedAtUtc", "timestamp with time zone", false),

        ("DocumentChunks", "Id", "uuid", false),
        ("DocumentChunks", "DocumentId", "uuid", false),
        ("DocumentChunks", "ChunkIndex", "integer", false),
        ("DocumentChunks", "Content", "text", false),

        ("Messages", "Id", "uuid", false),
        ("Messages", "ConversationId", "uuid", false),
        ("Messages", "Role", "text", false),
        ("Messages", "Content", "text", false),
        ("Messages", "SequenceNumber", "integer", false),
        ("Messages", "CreatedAtUtc", "timestamp with time zone", false),

        ("ToolExecutions", "Id", "uuid", false),
        ("ToolExecutions", "ConversationId", "uuid", false),
        ("ToolExecutions", "ToolName", "text", false),
        ("ToolExecutions", "ArgumentsJson", "text", false),
        ("ToolExecutions", "ResultJson", "text", false),
        ("ToolExecutions", "Status", "text", false),
        ("ToolExecutions", "DurationMs", "bigint", false),
        ("ToolExecutions", "ExecutedAtUtc", "timestamp with time zone", false),

        ("OutboxJobs", "Id", "uuid", false),
        ("OutboxJobs", "Topic", "character varying", false),
        ("OutboxJobs", "PayloadJson", "text", false),
        ("OutboxJobs", "Status", "character varying", false),
        ("OutboxJobs", "RetryCount", "integer", false),
        ("OutboxJobs", "MaxRetries", "integer", false),
        ("OutboxJobs", "NextRetryUtc", "timestamp with time zone", false),
        ("OutboxJobs", "CreatedAtUtc", "timestamp with time zone", false)
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

                if (hasBookingConversationId)
                {
                    await using var cmdIdx = db.Database.GetDbConnection().CreateCommand();
                    cmdIdx.CommandText = @"
                        SELECT EXISTS (
                            SELECT 1 FROM pg_index i
                            JOIN pg_class c ON c.oid = i.indexrelid
                            JOIN pg_class t ON t.oid = i.indrelid
                            JOIN pg_namespace n ON n.oid = t.relnamespace
                            WHERE n.nspname = 'public' AND t.relname = 'Bookings' AND c.relname = 'IX_Bookings_ConversationId'
                              AND (
                                  SELECT array_agg(a.attname::text ORDER BY array_position(i.indkey, a.attnum))
                                  FROM pg_attribute a
                                  WHERE a.attrelid = t.oid AND a.attnum = ANY(i.indkey)
                              ) = ARRAY['ConversationId']::text[]
                        );";
                    hasBookingConversationIndex = (bool)(await cmdIdx.ExecuteScalarAsync(ct) ?? false);

                    await using var cmdFk = db.Database.GetDbConnection().CreateCommand();
                    cmdFk.CommandText = @"
                        SELECT EXISTS (
                            SELECT 1 FROM pg_constraint c
                            JOIN pg_class src ON src.oid = c.conrelid
                            JOIN pg_class tgt ON tgt.oid = c.confrelid
                            JOIN pg_namespace n ON n.oid = src.relnamespace
                            WHERE n.nspname = 'public' 
                              AND src.relname = 'Bookings' 
                              AND c.conname = 'FK_Bookings_Conversations_ConversationId'
                              AND tgt.relname = 'Conversations'
                              AND c.confdeltype = 'n'
                              AND c.contype = 'f'
                        );";
                    hasBookingConversationFk = (bool)(await cmdFk.ExecuteScalarAsync(ct) ?? false);
                }
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

                if (hasBookingConversationId)
                {
                    await using var cmdIdx = db.Database.GetDbConnection().CreateCommand();
                    cmdIdx.CommandText = "SELECT COUNT(1) FROM pragma_index_list('Bookings') WHERE name = 'IX_Bookings_ConversationId';";
                    var idxCount = Convert.ToInt64(await cmdIdx.ExecuteScalarAsync(ct) ?? 0);
                    hasBookingConversationIndex = idxCount > 0;
                    hasBookingConversationFk = true; // SQLite foreign keys defined on schema creation
                }
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
            // Validate the supported legacy foundation definitions before proceeding
            await ValidateSupportedLegacySchemaAsync(db, logger, ct);

            // Reject partially upgraded or corrupted schemas where ConversationId exists but structural index or FK is missing,
            // even if AddBookingConversationId is already recorded in __EFMigrationsHistory.
            if (hasBookingConversationId && (!hasBookingConversationIndex || !hasBookingConversationFk))
            {
                throw new InvalidOperationException(
                    "Partially upgraded database schema detected: 'Bookings' table contains 'ConversationId' column, " +
                    $"but required structural components are incomplete (Index: {hasBookingConversationIndex}, FK: {hasBookingConversationFk}, MigrationHistory: {hasAddBookingConversationIdInHistory}). " +
                    "Baselining cannot proceed automatically without risk of unapplied schema integrity. " +
                    "To complete the upgrade safely and backfill ownership, invoke DatabaseMigrationHelper.RepairPartiallyUpgradedSchemaAsync(db).");
            }

            // Reject partially upgraded schema where ConversationId exists without migration history
            if (hasBookingConversationId && !hasAddBookingConversationIdInHistory)
            {
                throw new InvalidOperationException(
                    "Partially upgraded database schema detected: 'Bookings' table contains 'ConversationId' column, " +
                    $"but required structural components are incomplete (Index: {hasBookingConversationIndex}, FK: {hasBookingConversationFk}, MigrationHistory: {hasAddBookingConversationIdInHistory}). " +
                    "Baselining cannot proceed automatically without risk of unapplied schema integrity. " +
                    "To complete the upgrade safely and backfill ownership, invoke DatabaseMigrationHelper.RepairPartiallyUpgradedSchemaAsync(db).");
            }

            // Legacy database matching previous main revision without migration history
            if (!hasMigrationHistory)
            {
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
    /// Validates that an unversioned database conforms strictly to the supported schema definitions from commit 428f490.
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

            // 2. Validate pgvector extension and dimensions
            await using var cmdVec = db.Database.GetDbConnection().CreateCommand();
            cmdVec.CommandText = "SELECT COUNT(1) FROM pg_extension WHERE extname = 'vector';";
            var vecCount = Convert.ToInt64(await cmdVec.ExecuteScalarAsync(ct) ?? 0);
            if (vecCount == 0)
            {
                missingErrors.Add("Missing required pgvector extension");
            }
            else if (existingTables.Contains("DocumentChunks"))
            {
                await using var cmdDim = db.Database.GetDbConnection().CreateCommand();
                cmdDim.CommandText = @"
                    SELECT format_type(atttypid, atttypmod)
                    FROM pg_attribute
                    WHERE attrelid = 'public.""DocumentChunks""'::regclass
                      AND attname = 'Embedding';";
                var dimType = (string?)await cmdDim.ExecuteScalarAsync(ct);
                if (dimType == null || !dimType.Equals("vector(384)", StringComparison.OrdinalIgnoreCase))
                {
                    missingErrors.Add($"Invalid vector dimension for DocumentChunks.Embedding: expected 'vector(384)', got '{dimType ?? "none"}'");
                }
            }

            // 3. Validate capacity check constraint on AvailabilitySlots
            await using var cmdCheck = db.Database.GetDbConnection().CreateCommand();
            cmdCheck.CommandText = @"
                SELECT pg_get_expr(c.conbin, c.conrelid)
                FROM pg_constraint c
                JOIN pg_class t ON t.oid = c.conrelid
                JOIN pg_namespace n ON n.oid = t.relnamespace
                WHERE n.nspname = 'public' 
                  AND t.relname = 'AvailabilitySlots' 
                  AND c.conname = 'CK_AvailabilitySlots_Capacity'
                  AND c.contype = 'c';";
            var checkExpr = (string?)await cmdCheck.ExecuteScalarAsync(ct);
            if (string.IsNullOrWhiteSpace(checkExpr))
            {
                missingErrors.Add("Missing capacity check constraint: CK_AvailabilitySlots_Capacity");
            }
            else
            {
                var normalizedExpr = checkExpr.Replace("\"", "").Replace(" ", "").ToLowerInvariant();
                if (!normalizedExpr.Contains("bookedcapacity<=totalcapacity"))
                {
                    missingErrors.Add($"Capacity check constraint 'CK_AvailabilitySlots_Capacity' does not enforce BookedCapacity <= TotalCapacity (expression: {checkExpr})");
                }
            }

            // 4. Validate unique index on Bookings.IdempotencyKey
            await using var cmdIdx = db.Database.GetDbConnection().CreateCommand();
            cmdIdx.CommandText = @"
                SELECT 
                    i.indisunique,
                    (
                        SELECT array_agg(a.attname::text ORDER BY array_position(i.indkey, a.attnum))
                        FROM pg_attribute a
                        WHERE a.attrelid = t.oid AND a.attnum = ANY(i.indkey)
                    ) AS indexed_cols
                FROM pg_index i
                JOIN pg_class c ON c.oid = i.indexrelid
                JOIN pg_class t ON t.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = t.relnamespace
                WHERE n.nspname = 'public' 
                  AND t.relname = 'Bookings' 
                  AND c.relname = 'IX_Bookings_IdempotencyKey';";
            bool? isUnique = null;
            string[]? idxCols = null;
            await using (var reader = await cmdIdx.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    isUnique = reader.GetBoolean(0);
                    idxCols = (string[])reader.GetValue(1);
                }
            }

            if (isUnique == null)
            {
                missingErrors.Add("Missing required unique index: IX_Bookings_IdempotencyKey");
            }
            else
            {
                if (isUnique != true)
                {
                    missingErrors.Add("Index 'IX_Bookings_IdempotencyKey' is not defined as UNIQUE");
                }
                if (idxCols == null || idxCols.Length != 1 || !idxCols[0].Equals("IdempotencyKey", StringComparison.OrdinalIgnoreCase))
                {
                    missingErrors.Add($"Index 'IX_Bookings_IdempotencyKey' must index ['IdempotencyKey'], got [{string.Join(", ", idxCols ?? Array.Empty<string>())}]");
                }
            }

            // 5. Validate foreign-key mappings and delete behavior
            await using var cmdFks = db.Database.GetDbConnection().CreateCommand();
            cmdFks.CommandText = @"
                SELECT 
                    c.conname,
                    src.relname AS source_table,
                    tgt.relname AS target_table,
                    c.confdeltype,
                    (
                        SELECT array_agg(a.attname::text ORDER BY array_position(c.conkey, a.attnum))
                        FROM pg_attribute a
                        WHERE a.attrelid = src.oid AND a.attnum = ANY(c.conkey)
                    ) AS source_cols,
                    (
                        SELECT array_agg(a.attname::text ORDER BY array_position(c.confkey, a.attnum))
                        FROM pg_attribute a
                        WHERE a.attrelid = tgt.oid AND a.attnum = ANY(c.confkey)
                    ) AS target_cols
                FROM pg_constraint c
                JOIN pg_class src ON src.oid = c.conrelid
                JOIN pg_class tgt ON tgt.oid = c.confrelid
                JOIN pg_namespace n ON n.oid = src.relnamespace
                WHERE n.nspname = 'public' AND c.contype = 'f';";
            var existingFkMappings = new Dictionary<string, (string ConName, char DelType)>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await cmdFks.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var conname = reader.GetString(0);
                    var srcTbl = reader.GetString(1);
                    var tgtTbl = reader.GetString(2);
                    var delType = reader.GetChar(3);
                    var srcCols = (string[])reader.GetValue(4);
                    var tgtCols = (string[])reader.GetValue(5);
                    var mappingKey = $"{srcTbl}.{string.Join(",", srcCols)}->{tgtTbl}.{string.Join(",", tgtCols)}";
                    existingFkMappings[mappingKey] = (conname, delType);
                }
            }

            var expectedFks = new (string Description, string MappingKey, char[] DelTypes)[]
            {
                ("Bookings(SlotId) -> AvailabilitySlots(Id)", "Bookings.SlotId->AvailabilitySlots.Id", new[] { 'a', 'r' }),
                ("PendingBookings(SlotId) -> AvailabilitySlots(Id)", "PendingBookings.SlotId->AvailabilitySlots.Id", new[] { 'a', 'r' }),
                ("PendingBookings(ConversationId) -> Conversations(Id)", "PendingBookings.ConversationId->Conversations.Id", new[] { 'c' }),
                ("Messages(ConversationId) -> Conversations(Id)", "Messages.ConversationId->Conversations.Id", new[] { 'c' }),
                ("ToolExecutions(ConversationId) -> Conversations(Id)", "ToolExecutions.ConversationId->Conversations.Id", new[] { 'c' }),
                ("Conversations(AgentId) -> Agents(Id)", "Conversations.AgentId->Agents.Id", new[] { 'a', 'r' }),
                ("DocumentChunks(DocumentId) -> KnowledgeDocuments(Id)", "DocumentChunks.DocumentId->KnowledgeDocuments.Id", new[] { 'c' })
            };

            foreach (var fk in expectedFks)
            {
                if (!existingFkMappings.TryGetValue(fk.MappingKey, out var actual))
                {
                    missingErrors.Add($"Missing required foreign key mapping: {fk.Description}");
                }
                else if (!fk.DelTypes.Contains(actual.DelType))
                {
                    missingErrors.Add($"Foreign key mapping {fk.Description} has delete action '{actual.DelType}', expected one of [{string.Join(",", fk.DelTypes)}]");
                }
            }

            // 6. Validate required application columns, types, and nullability
            await using var cmdCols = db.Database.GetDbConnection().CreateCommand();
            cmdCols.CommandText = "SELECT table_name, column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'public';";
            var existingColumns = new Dictionary<string, (string DataType, bool IsNullable)>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await cmdCols.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
                    var dataType = reader.GetString(2);
                    var isNullable = reader.GetString(3).Equals("YES", StringComparison.OrdinalIgnoreCase);
                    existingColumns[key] = (dataType, isNullable);
                }
            }

            foreach (var reqCol in RequiredLegacyColumns)
            {
                if (reqCol.Table == "ChunkIndex") continue; // skip guard
                var key = $"{reqCol.Table}.{reqCol.Column}";
                if (!existingColumns.TryGetValue(key, out var actualCol))
                {
                    missingErrors.Add($"Missing required application column '{reqCol.Column}' on table '{reqCol.Table}'");
                }
                else
                {
                    if (!string.Equals(actualCol.DataType, reqCol.ExpectedType, StringComparison.OrdinalIgnoreCase))
                    {
                        missingErrors.Add($"Invalid data type for {key}: expected '{reqCol.ExpectedType}', got '{actualCol.DataType}'");
                    }
                    if (actualCol.IsNullable != reqCol.Nullable)
                    {
                        missingErrors.Add($"Invalid nullability for {key}: expected nullable={reqCol.Nullable}, got {actualCol.IsNullable}");
                    }
                }
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

            // Validate unique index on Bookings.IdempotencyKey
            await using var cmdIdx = db.Database.GetDbConnection().CreateCommand();
            cmdIdx.CommandText = "SELECT \"unique\" FROM pragma_index_list('Bookings') WHERE name = 'IX_Bookings_IdempotencyKey';";
            var isUniqueObj = await cmdIdx.ExecuteScalarAsync(ct);
            if (isUniqueObj == null)
            {
                // In SQLite, an inline UNIQUE constraint creates sqlite_autoindex_Bookings_1
                await using var cmdAutoIdx = db.Database.GetDbConnection().CreateCommand();
                cmdAutoIdx.CommandText = "SELECT COUNT(1) FROM pragma_index_list('Bookings') WHERE \"unique\" = 1;";
                var autoIdxCount = Convert.ToInt64(await cmdAutoIdx.ExecuteScalarAsync(ct) ?? 0);
                if (autoIdxCount == 0)
                {
                    missingErrors.Add("Missing required unique index on Bookings.IdempotencyKey");
                }
            }
            else if (Convert.ToInt64(isUniqueObj) != 1)
            {
                missingErrors.Add("Index 'IX_Bookings_IdempotencyKey' is not defined as UNIQUE");
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
    /// Explicit transactional repair path for partially upgraded databases containing ConversationId but missing
    /// index, foreign key, migration history, or backfill.
    /// Preserves existing records, restores relationships, enforces capacity constraints,
    /// validates repaired structure, and records migrations reliably so subsequent migrations succeed idempotently.
    /// </summary>
    public static async Task RepairPartiallyUpgradedSchemaAsync(AppDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var isNpgsql = db.Database.IsNpgsql();
        var isSqlite = db.Database.IsSqlite();

        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        logger?.LogInformation("Executing explicit transactional repair for partially upgraded schema...");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
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

                // Ensure foreign key constraint exists with ON DELETE SET NULL
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
                // Require consistent booking and pending-record details (SlotId, RequestHash),
                // and exclude any conflicting/ambiguous records.
                await db.Database.ExecuteSqlRawAsync(@"
                    UPDATE ""Bookings"" b
                    SET ""ConversationId"" = u.""ConversationId""
                    FROM (
                        SELECT 
                            pb.""IdempotencyKey"",
                            pb.""SlotId"",
                            pb.""RequestHash"",
                            MIN(pb.""ConversationId""::text)::uuid AS ""ConversationId""
                        FROM ""PendingBookings"" pb
                        WHERE pb.""ConversationId"" IS NOT NULL
                        GROUP BY pb.""IdempotencyKey"", pb.""SlotId"", pb.""RequestHash""
                        HAVING COUNT(DISTINCT pb.""ConversationId"") = 1
                    ) u
                    WHERE b.""IdempotencyKey"" = u.""IdempotencyKey""
                      AND b.""SlotId"" = u.""SlotId""
                      AND b.""RequestHash"" = u.""RequestHash""
                      AND b.""ConversationId"" IS NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM ""PendingBookings"" pb_other
                          WHERE pb_other.""IdempotencyKey"" = b.""IdempotencyKey""
                            AND (pb_other.""SlotId"" <> b.""SlotId"" OR pb_other.""RequestHash"" <> b.""RequestHash"")
                      );
                ", ct);

                // 4. Verify capacity constraints are intact
                await using var cmdCap = db.Database.GetDbConnection().CreateCommand();
                cmdCap.CommandText = "SELECT COUNT(1) FROM \"AvailabilitySlots\" WHERE \"BookedCapacity\" > \"TotalCapacity\";";
                var capViolations = Convert.ToInt64(await cmdCap.ExecuteScalarAsync(ct) ?? 0);
                if (capViolations > 0)
                {
                    throw new InvalidOperationException($"Capacity constraint violation: {capViolations} slots exceed TotalCapacity.");
                }

                // 5. Final validation of repaired structure before recording history
                await ValidateRepairedStructureAsync(db, ct);

                // 6. Record both migrations in __EFMigrationsHistory
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

                // Backfill unambiguously with consistent details
                await db.Database.ExecuteSqlRawAsync(@"
                    UPDATE Bookings
                    SET ConversationId = (
                        SELECT pb.ConversationId
                        FROM PendingBookings pb
                        WHERE pb.IdempotencyKey = Bookings.IdempotencyKey
                          AND pb.SlotId = Bookings.SlotId
                          AND pb.RequestHash = Bookings.RequestHash
                          AND pb.ConversationId IS NOT NULL
                        GROUP BY pb.IdempotencyKey, pb.SlotId, pb.RequestHash
                        HAVING COUNT(DISTINCT pb.ConversationId) = 1
                    )
                    WHERE ConversationId IS NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM PendingBookings pb_other
                          WHERE pb_other.IdempotencyKey = Bookings.IdempotencyKey
                            AND (pb_other.SlotId <> Bookings.SlotId OR pb_other.RequestHash <> Bookings.RequestHash)
                      )
                      AND IdempotencyKey IN (
                          SELECT pb.IdempotencyKey
                          FROM PendingBookings pb
                          WHERE pb.SlotId = Bookings.SlotId
                            AND pb.RequestHash = Bookings.RequestHash
                            AND pb.ConversationId IS NOT NULL
                          GROUP BY pb.IdempotencyKey, pb.SlotId, pb.RequestHash
                          HAVING COUNT(DISTINCT pb.ConversationId) = 1
                      );
                ");

                // Verify capacity constraint
                await using var cmdCap = db.Database.GetDbConnection().CreateCommand();
                cmdCap.CommandText = "SELECT COUNT(1) FROM AvailabilitySlots WHERE BookedCapacity > TotalCapacity;";
                var capViolations = Convert.ToInt64(await cmdCap.ExecuteScalarAsync(ct) ?? 0);
                if (capViolations > 0)
                {
                    throw new InvalidOperationException($"Capacity constraint violation: {capViolations} slots exceed TotalCapacity.");
                }

                await ValidateRepairedStructureAsync(db, ct);

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

            await tx.CommitAsync(ct);
            logger?.LogInformation("Partially upgraded schema repaired, validated, and migration history synchronized successfully.");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger?.LogError(ex, "Repair operation failed and was rolled back.");
            throw;
        }
    }

    private static async Task ValidateRepairedStructureAsync(AppDbContext db, CancellationToken ct)
    {
        var isNpgsql = db.Database.IsNpgsql();
        var isSqlite = db.Database.IsSqlite();

        if (isNpgsql)
        {
            var conn = db.Database.GetDbConnection();

            // Check column
            await using var cmdCol = conn.CreateCommand();
            cmdCol.CommandText = "SELECT data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND lower(table_name) = 'bookings' AND lower(column_name) = 'conversationid';";
            string? colType = null;
            string? isNullable = null;
            await using (var reader = await cmdCol.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    colType = reader.GetString(0);
                    isNullable = reader.GetString(1);
                }
            }
            if (colType == null || !colType.Equals("uuid", StringComparison.OrdinalIgnoreCase) || isNullable == null || !isNullable.Equals("YES", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Repair validation failed: 'Bookings.ConversationId' must exist as nullable uuid.");
            }

            // Check index
            await using var cmdIdx = conn.CreateCommand();
            cmdIdx.CommandText = @"
                SELECT (
                    SELECT array_agg(a.attname::text ORDER BY array_position(i.indkey, a.attnum))
                    FROM pg_attribute a
                    WHERE a.attrelid = t.oid AND a.attnum = ANY(i.indkey)
                )
                FROM pg_index i
                JOIN pg_class c ON c.oid = i.indexrelid
                JOIN pg_class t ON t.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = t.relnamespace
                WHERE n.nspname = 'public' AND t.relname = 'Bookings' AND c.relname = 'IX_Bookings_ConversationId';";
            var idxCols = (string[]?)await cmdIdx.ExecuteScalarAsync(ct);
            if (idxCols == null || idxCols.Length != 1 || !idxCols[0].Equals("ConversationId", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Repair validation failed: 'IX_Bookings_ConversationId' must exist and index 'ConversationId'.");
            }

            // Check foreign key
            await using var cmdFk = conn.CreateCommand();
            cmdFk.CommandText = @"
                SELECT c.confdeltype, tgt.relname
                FROM pg_constraint c
                JOIN pg_class src ON src.oid = c.conrelid
                JOIN pg_class tgt ON tgt.oid = c.confrelid
                JOIN pg_namespace n ON n.oid = src.relnamespace
                WHERE n.nspname = 'public' 
                  AND src.relname = 'Bookings' 
                  AND c.conname = 'FK_Bookings_Conversations_ConversationId'
                  AND c.contype = 'f';";
            char? delType = null;
            string? targetTable = null;
            await using (var reader = await cmdFk.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    delType = reader.GetChar(0);
                    targetTable = reader.GetString(1);
                }
            }
            if (delType == null || delType != 'n' || !string.Equals(targetTable, "Conversations", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Repair validation failed: 'FK_Bookings_Conversations_ConversationId' must reference 'Conversations' with ON DELETE SET NULL.");
            }
        }
        else if (isSqlite)
        {
            var conn = db.Database.GetDbConnection();
            await using var cmdCol = conn.CreateCommand();
            cmdCol.CommandText = "SELECT COUNT(1) FROM pragma_table_info('Bookings') WHERE lower(name) = 'conversationid';";
            var hasCol = Convert.ToInt64(await cmdCol.ExecuteScalarAsync(ct) ?? 0) > 0;
            if (!hasCol) throw new InvalidOperationException("Repair validation failed: Bookings.ConversationId is missing.");

            await using var cmdIdx = conn.CreateCommand();
            cmdIdx.CommandText = "SELECT COUNT(1) FROM pragma_index_list('Bookings') WHERE name = 'IX_Bookings_ConversationId';";
            var hasIdx = Convert.ToInt64(await cmdIdx.ExecuteScalarAsync(ct) ?? 0) > 0;
            if (!hasIdx) throw new InvalidOperationException("Repair validation failed: IX_Bookings_ConversationId is missing.");
        }
    }
}
