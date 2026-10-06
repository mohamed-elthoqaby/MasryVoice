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

            // Backfill ownership exclusively from reliable relationships (IdempotencyKey matching PendingBooking).
            // Explicitly avoid inferring ownership from phone numbers to prevent cross-customer data leakage.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql(@"
                    UPDATE ""Bookings"" b
                    SET ""ConversationId"" = pb.""ConversationId""
                    FROM ""PendingBookings"" pb
                    WHERE b.""IdempotencyKey"" = pb.""IdempotencyKey""
                      AND pb.""ConversationId"" IS NOT NULL;
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
                        LIMIT 1
                    )
                    WHERE ConversationId IS NULL
                      AND EXISTS (
                        SELECT 1
                        FROM PendingBookings pb
                        WHERE pb.IdempotencyKey = Bookings.IdempotencyKey
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
