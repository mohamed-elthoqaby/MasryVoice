using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasryVoice.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingConversationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_ConversationId",
                table: "Bookings",
                column: "ConversationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_Conversations_ConversationId",
                table: "Bookings",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Backfill ownership exclusively from unambiguous, reliable relationships (IdempotencyKey matching PendingBooking).
            // Explicitly avoid inferring ownership from phone numbers to prevent cross-customer data leakage.
            // Requires consistent booking and pending-record details (SlotId, RequestHash), and excludes conflicting records.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql(@"
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
                ");
            }
            else
            {
                migrationBuilder.Sql(@"
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
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_Conversations_ConversationId",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_ConversationId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "Bookings");
        }
    }
}
