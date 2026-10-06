using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MasryVoice.Api.Common;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Infrastructure.Persistence;

namespace MasryVoice.Api.Features.Bookings;

public record ConfirmationResult(
    bool Success,
    string Message,
    string? ErrorCode = null,
    object? Data = null
);

public interface IBookingConfirmationService
{
    Task<ConfirmationResult> ConfirmPendingBookingAsync(
        Guid conversationId,
        Guid pendingBookingId,
        string? expectedRequestHash = null,
        CancellationToken ct = default);
}

/// <summary>
/// Server-side application service invoked exclusively by the customer-facing confirmation action.
/// This operation is NOT exposed as an LLM-callable tool to maintain an uncompromising trust boundary.
/// Enforces conversation-binding, pending ID verification, stale-hash rejection, application idempotency,
/// and atomic engine-level capacity protection.
/// </summary>
public class BookingConfirmationService : IBookingConfirmationService
{
    private readonly AppDbContext _db;

    public BookingConfirmationService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ConfirmationResult> ConfirmPendingBookingAsync(
        Guid conversationId,
        Guid pendingBookingId,
        string? expectedRequestHash = null,
        CancellationToken ct = default)
    {
        // 1. Locate PendingBooking by explicit pending ID
        var pending = await _db.PendingBookings
            .FirstOrDefaultAsync(pb => pb.Id == pendingBookingId, ct);

        if (pending == null)
        {
            return new ConfirmationResult(
                Success: false,
                ErrorCode: "PENDING_NOT_FOUND",
                Message: "طلب الحجز المعلق غير موجود في النظام."
            );
        }

        // 2. Strict conversation binding
        if (pending.ConversationId != conversationId)
        {
            return new ConfirmationResult(
                Success: false,
                ErrorCode: "CONVERSATION_MISMATCH",
                Message: "طلب الحجز المعلق لا ينتمي إلى هذه المحادثة."
            );
        }

        // 3. Stale details check via cryptographic RequestHash
        if (!string.IsNullOrWhiteSpace(expectedRequestHash) &&
            !string.Equals(expectedRequestHash, pending.RequestHash, StringComparison.OrdinalIgnoreCase))
        {
            return new ConfirmationResult(
                Success: false,
                ErrorCode: "STALE_BOOKING_DETAILS",
                Message: "بيانات الحجز المعروضة غير مطابقة للبيانات المسجلة. يرجى مراجعة البطاقة المحدثة."
            );
        }

        // 4. Application-level Idempotency Check (handles repeated or concurrent confirmations)
        var existingBooking = await _db.Bookings
            .Include(b => b.Slot)
            .FirstOrDefaultAsync(b => b.IdempotencyKey == pending.IdempotencyKey, ct);

        if (existingBooking != null)
        {
            // Verify request hash to prevent reusing key with different booking details
            if (existingBooking.RequestHash != pending.RequestHash)
            {
                return new ConfirmationResult(
                    Success: false,
                    ErrorCode: "IDEMPOTENCY_CONFLICT",
                    Message: "تم رفض الطلب: تم استخدام مفتاح الحجز مسبقاً ببيانات مختلفة. لا يمكن تعديل بيانات الحجز تحت نفس المفتاح."
                );
            }

            // Repeated confirmation with identical details returns the original booking
            return new ConfirmationResult(
                Success: true,
                Message: $"الحجز مسجل بالفعل مسبقاً برقم {existingBooking.Id}. تم إرجاع الحجز الأصلي.",
                Data: new
                {
                    bookingId = existingBooking.Id,
                    customerName = existingBooking.CustomerName,
                    customerPhone = existingBooking.CustomerPhone,
                    service = existingBooking.ServiceName,
                    cairoTime = CairoTimeHelper.FormatCairoFriendly(existingBooking.BookingDateUtc),
                    status = existingBooking.Status,
                    isDuplicateReplayed = true
                }
            );
        }

        // 5. Status checks: reject invalidated or already modified status
        if (pending.Status == "Invalidated")
        {
            return new ConfirmationResult(
                Success: false,
                ErrorCode: "STALE_PENDING_BOOKING",
                Message: "تم إبطال هذا الحجز المعلق نظراً لتغيير بيانات الحجز. يرجى تأكيد البطاقة المحدثة."
            );
        }

        if (pending.Status != "Pending")
        {
            return new ConfirmationResult(
                Success: false,
                ErrorCode: "INVALID_STATUS",
                Message: $"لا يمكن تأكيد الحجز لأن حالته الحالية هي '{pending.Status}'."
            );
        }

        // 6. ACID Transaction with atomic database-level capacity reservation
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Atomic conditional update at database engine level:
            // Locks the slot row, ensures capacity is not exceeded, and prevents lost updates.
            var affected = await _db.Database.ExecuteSqlRawAsync(
                "UPDATE \"AvailabilitySlots\" SET \"BookedCapacity\" = \"BookedCapacity\" + 1 WHERE \"Id\" = {0} AND \"BookedCapacity\" < \"TotalCapacity\"",
                new object[] { pending.SlotId },
                ct);

            if (affected == 0)
            {
                var slotExists = await _db.AvailabilitySlots.AnyAsync(s => s.Id == pending.SlotId, ct);
                if (!slotExists)
                {
                    await tx.RollbackAsync(ct);
                    return new ConfirmationResult(
                        Success: false,
                        ErrorCode: "SLOT_NOT_FOUND",
                        Message: "الموعد المطلوب لم يعد متاحاً."
                    );
                }

                pending.Status = "FailedCapacity";
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new ConfirmationResult(
                    Success: false,
                    ErrorCode: "SLOT_FULLY_BOOKED",
                    Message: "عذراً يا فندم، تم حجز الموعد بالكامل بواسطة عميل آخر قبل تأكيدك. يرجى اختيار موعد آخر."
                );
            }

            var newBooking = new Booking
            {
                Id = Guid.NewGuid(),
                SlotId = pending.SlotId,
                CustomerName = pending.CustomerName,
                CustomerPhone = pending.CustomerPhone,
                ServiceName = pending.ServiceName,
                BookingDateUtc = pending.BookingDateUtc,
                Status = "Confirmed",
                IdempotencyKey = pending.IdempotencyKey,
                RequestHash = pending.RequestHash,
                CreatedAtUtc = DateTime.UtcNow
            };

            pending.Status = "Confirmed";
            pending.ConfirmedAtUtc = DateTime.UtcNow;

            _db.Bookings.Add(newBooking);

            // Atomically enqueue durable outbox job inside the exact same database transaction
            _db.OutboxJobs.Add(new OutboxJob
            {
                Id = Guid.NewGuid(),
                Topic = "BookingConfirmed",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    bookingId = newBooking.Id,
                    customerName = newBooking.CustomerName,
                    customerPhone = newBooking.CustomerPhone,
                    serviceName = newBooking.ServiceName,
                    bookingDateUtc = newBooking.BookingDateUtc
                }),
                Status = "Pending",
                CreatedAtUtc = DateTime.UtcNow,
                NextRetryUtc = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Synchronize tracked slot in change tracker if present in current DbContext
            var trackedSlot = _db.ChangeTracker.Entries<AvailabilitySlot>()
                .FirstOrDefault(e => e.Entity.Id == pending.SlotId);
            if (trackedSlot != null)
            {
                await trackedSlot.ReloadAsync(ct);
            }

            return new ConfirmationResult(
                Success: true,
                Message: $"تم تأكيد الحجز بنجاح برقم {newBooking.Id}!",
                Data: new
                {
                    bookingId = newBooking.Id,
                    customerName = newBooking.CustomerName,
                    customerPhone = newBooking.CustomerPhone,
                    service = newBooking.ServiceName,
                    cairoTime = CairoTimeHelper.FormatCairoFriendly(newBooking.BookingDateUtc),
                    status = newBooking.Status
                }
            );
        });
    }
}
