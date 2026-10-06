using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Infrastructure.Persistence;

/// <summary>
/// Safe database migration helper for production and development deployments.
/// Replaces unversioned EnsureCreatedAsync with safe, reversible EF Core migrations.
/// Provides automatic baseline capability for existing databases previously initialized
/// via EnsureCreated (preserving existing customer data and backfilling relationships reliably).
/// </summary>
public static class DatabaseMigrationHelper
{
    public static async Task ApplyMigrationsAsync(AppDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var isNpgsql = db.Database.IsNpgsql();
        var isSqlite = db.Database.IsSqlite();

        bool hasLegacyTables = false;
        bool hasMigrationHistory = false;
        bool hasBookingConversationId = false;

        if (isNpgsql)
        {
            await using var cmdHist = db.Database.GetDbConnection().CreateCommand();
            cmdHist.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory');";
            if (cmdHist.Connection?.State != System.Data.ConnectionState.Open)
            {
                await db.Database.OpenConnectionAsync(ct);
            }
            hasMigrationHistory = (bool)(await cmdHist.ExecuteScalarAsync(ct) ?? false);

            await using var cmdBookings = db.Database.GetDbConnection().CreateCommand();
            cmdBookings.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Bookings');";
            hasLegacyTables = (bool)(await cmdBookings.ExecuteScalarAsync(ct) ?? false);

            if (hasLegacyTables)
            {
                await using var cmdCol = db.Database.GetDbConnection().CreateCommand();
                cmdCol.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND lower(table_name) = 'bookings' AND lower(column_name) = 'conversationid');";
                hasBookingConversationId = (bool)(await cmdCol.ExecuteScalarAsync(ct) ?? false);
            }
        }
        else if (isSqlite)
        {
            await using var cmdHist = db.Database.GetDbConnection().CreateCommand();
            cmdHist.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
            if (cmdHist.Connection?.State != System.Data.ConnectionState.Open)
            {
                await db.Database.OpenConnectionAsync(ct);
            }
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

        // If existing database was created via EnsureCreated or contains pre-existing schema changes, baseline them
        if (hasLegacyTables)
        {
            var migrations = db.Database.GetMigrations().ToList();
            var initialCreateId = migrations.FirstOrDefault(m => m.EndsWith("_InitialCreate"));
            var addBookingConvId = migrations.FirstOrDefault(m => m.EndsWith("_AddBookingConversationId"));

            var migrationsToBaseline = new List<string>();
            if (!hasMigrationHistory && !string.IsNullOrEmpty(initialCreateId))
            {
                migrationsToBaseline.Add(initialCreateId);
            }
            if (hasBookingConversationId && !string.IsNullOrEmpty(addBookingConvId))
            {
                migrationsToBaseline.Add(addBookingConvId);
            }

            if (migrationsToBaseline.Count > 0)
            {
                logger?.LogInformation("Baselining migrations into __EFMigrationsHistory without dropping customer data...");
                foreach (var migId in migrationsToBaseline)
                {
                    if (isNpgsql)
                    {
                        await db.Database.ExecuteSqlAsync($@"
                            CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                                ""MigrationId"" character varying(150) NOT NULL,
                                ""ProductVersion"" character varying(32) NOT NULL,
                                CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                            );
                            INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                            VALUES ({migId}, '10.0.12')
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
                            VALUES ({migId}, '10.0.12');
                        ", ct);
                    }
                    logger?.LogInformation("Successfully baselined migration ({MigrationId})", migId);
                }
            }
        }

        logger?.LogInformation("Applying EF Core database migrations...");
        await db.Database.MigrateAsync(ct);
        logger?.LogInformation("EF Core database migrations applied successfully.");
    }
}
