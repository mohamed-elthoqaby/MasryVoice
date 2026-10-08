using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MasryVoice.Api.Infrastructure.Persistence;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Common;

namespace MasryVoice.Api.Features.Tools;

public class CheckAvailabilityTool : ITool
{
    private readonly AppDbContext _db;

    public CheckAvailabilityTool(AppDbContext db)
    {
        _db = db;
    }

    public ToolDefinition Definition => new()
    {
        Name = "CheckAvailability",
        Description = "Check available clinic appointment slots in Cairo time. Use when customer asks about available times, schedule, or dates. (استعلام عن المواعيد المتاحة)",
        Parameters = new()
        {
            ["date"] = new("string", "Date string such as YYYY-MM-DD or 'tomorrow' or 'today'", Required: false),
            ["service"] = new("string", "Requested medical specialty or service name", Required: false)
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, Guid conversationId, CancellationToken ct)
    {
        var nowCairo = CairoTimeHelper.NowCairo;
        var targetDate = nowCairo.Date;

        string? requestedService = null;
        if (arguments.TryGetProperty("service", out var servProp) && !string.IsNullOrWhiteSpace(servProp.GetString()))
        {
            requestedService = servProp.GetString()!.Trim();
        }

        if (arguments.TryGetProperty("date", out var dateProp) && !string.IsNullOrWhiteSpace(dateProp.GetString()))
        {
            var dateStr = dateProp.GetString()!.Trim();
            if (EgyptianDateTimeParser.TryParseEgyptianDate(dateStr, nowCairo, out var parsedDate))
            {
                targetDate = parsedDate.Date;
            }
        }

        var dayStartUtc = CairoTimeHelper.CairoToUtc(targetDate);
        var dayEndUtc = CairoTimeHelper.CairoToUtc(targetDate.AddDays(1));

        var query = _db.AvailabilitySlots
            .Where(s => s.StartTimeUtc >= dayStartUtc && s.StartTimeUtc < dayEndUtc && s.BookedCapacity < s.TotalCapacity);

        if (!string.IsNullOrWhiteSpace(requestedService))
        {
            var filter = requestedService.ToLowerInvariant();
            query = query.Where(s => s.ServiceName.ToLower().Contains(filter));
        }

        var availableSlots = await query
            .OrderBy(s => s.StartTimeUtc)
            .ToListAsync(ct);

        if (availableSlots.Count == 0)
        {
            var fallbackQuery = _db.AvailabilitySlots
                .Where(s => s.StartTimeUtc >= DateTime.UtcNow && s.BookedCapacity < s.TotalCapacity);

            if (!string.IsNullOrWhiteSpace(requestedService))
            {
                var filter = requestedService.ToLowerInvariant();
                fallbackQuery = fallbackQuery.Where(s => s.ServiceName.ToLower().Contains(filter));
            }

            var nextSlots = await fallbackQuery
                .OrderBy(s => s.StartTimeUtc)
                .Take(4)
                .ToListAsync(ct);

            if (nextSlots.Count == 0)
            {
                return new ToolResult(
                    Success: true,
                    Message: "لا توجد أي مواعيد متاحة حالياً في النظام.",
                    Data: new { availableSlots = Array.Empty<object>() }
                );
            }

            var nextSlotDtos = nextSlots.Select(s => new
            {
                slotId = s.Id,
                service = s.ServiceName,
                cairoTime = CairoTimeHelper.FormatCairoFriendly(s.StartTimeUtc),
                startTimeUtc = s.StartTimeUtc
            }).ToList();

            return new ToolResult(
                Success: true,
                Message: $"لا توجد مواعيد متاحة في تاريخ {targetDate:yyyy-MM-dd}، ولكن توجد أقرب مواعيد أخرى متاحة.",
                Data: new { availableSlots = nextSlotDtos }
            );
        }

        var slotDtos = availableSlots.Select(s => new
        {
            slotId = s.Id,
            service = s.ServiceName,
            cairoTime = CairoTimeHelper.FormatCairoFriendly(s.StartTimeUtc),
            startTimeUtc = s.StartTimeUtc
        }).ToList();

        return new ToolResult(
            Success: true,
            Message: $"تم العثور على {slotDtos.Count} موعد متاح في {targetDate:yyyy-MM-dd}.",
            Data: new { availableSlots = slotDtos }
        );
    }
}

/// <summary>
/// Tool 1/2 of Booking Lifecycle: Stages a pending booking record tied to the conversation.
/// Does NOT commit the final booking until explicit confirmation is performed.
/// Any change in details automatically invalidates previously staged confirmations.
/// </summary>
public class StageBookingTool : ITool
{
    private readonly AppDbContext _db;

    public StageBookingTool(AppDbContext db)
    {
        _db = db;
    }

    public ToolDefinition Definition => new()
    {
        Name = "StageBooking",
        Description = "Stage a pending booking draft in the database when the customer requests to book an appointment and provides their name and phone number. Always call this tool to prepare the booking before confirmation. (تجهيز مسودة حجز معلق)",
        Parameters = new()
        {
            ["customerName"] = new("string", "Full name of the customer/patient (e.g. Mohamed Atef)", Required: true),
            ["customerPhone"] = new("string", "Phone number of the customer (e.g. 01012345678)", Required: true),
            ["slotId"] = new("string", "Optional slot ID if chosen", Required: false),
            ["serviceName"] = new("string", "Optional service name (e.g. General Consultation)", Required: false)
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, Guid conversationId, CancellationToken ct)
    {
        // 1. Validate customer name
        if (!arguments.TryGetProperty("customerName", out var nameProp) || string.IsNullOrWhiteSpace(nameProp.GetString()))
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "MISSING_CUSTOMER_NAME",
                Message: "خطأ: اسم العميل مطلوب لتجهيز الحجز."
            );
        }
        var customerName = nameProp.GetString()!.Trim();
        if (customerName.Length < 2)
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "INVALID_CUSTOMER_NAME",
                Message: "خطأ: يرجى كتابة الاسم بالكامل لتسجيل الحجز."
            );
        }

        // 2. Validate customer phone with Egyptian normalization
        if (!arguments.TryGetProperty("customerPhone", out var phoneProp) || string.IsNullOrWhiteSpace(phoneProp.GetString()))
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "MISSING_CUSTOMER_PHONE",
                Message: "خطأ: رقم تليفون العميل مطلوب لتجهيز الحجز."
            );
        }
        var rawPhone = phoneProp.GetString()!.Trim();
        var normalizedPhone = EgyptianDateTimeParser.NormalizeEgyptianPhone(rawPhone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "INVALID_PHONE_NUMBER",
                Message: "خطأ: رقم التليفون غير صحيح. يرجى إدخال رقم محمول مصري مكون من 11 رقماً يبدأ بـ 01 (مثل 01012345678)."
            );
        }
        var customerPhone = normalizedPhone;

        // 3. Resolve slot
        Guid slotId = Guid.Empty;
        if (arguments.TryGetProperty("slotId", out var slotIdProp) && Guid.TryParse(slotIdProp.GetString(), out var parsedGuid))
        {
            slotId = parsedGuid;
        }

        AvailabilitySlot? slot;
        if (slotId != Guid.Empty)
        {
            slot = await _db.AvailabilitySlots.FirstOrDefaultAsync(s => s.Id == slotId, ct);
        }
        else
        {
            slot = await _db.AvailabilitySlots
                .Where(s => s.BookedCapacity < s.TotalCapacity && s.StartTimeUtc >= DateTime.UtcNow)
                .OrderBy(s => s.StartTimeUtc)
                .FirstOrDefaultAsync(ct);
        }

        if (slot == null)
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "SLOT_NOT_FOUND",
                Message: "الموعد المطلوب غير متوفر أو غير موجود."
            );
        }

        if (slot.BookedCapacity >= slot.TotalCapacity)
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "SLOT_FULLY_BOOKED",
                Message: "عذراً يا فندم، هذا الموعد تم حجزه بالكامل. يرجى اختيار موعد آخر متاح."
            );
        }

        var serviceName = arguments.TryGetProperty("serviceName", out var servProp) && !string.IsNullOrWhiteSpace(servProp.GetString())
            ? servProp.GetString()!
            : slot.ServiceName;

        // Compute request hash and application-level idempotency key
        var requestHash = PendingBooking.ComputeRequestHash(slot.Id, customerPhone, customerName, serviceName);
        var idempotencyKey = $"idemp_{conversationId:N}_{slot.Id:N}_{customerPhone}";

        // 4. Invalidate any existing pending booking for this conversation if details changed
        var existingPending = await _db.PendingBookings
            .Where(pb => pb.ConversationId == conversationId && pb.Status == "Pending")
            .ToListAsync(ct);

        foreach (var p in existingPending)
        {
            if (p.RequestHash != requestHash)
            {
                p.Status = "Invalidated"; // Changing details invalidates previous staging!
            }
        }

        // Create new PendingBooking
        var pendingBooking = new PendingBooking
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SlotId = slot.Id,
            CustomerName = customerName,
            CustomerPhone = customerPhone,
            ServiceName = serviceName,
            BookingDateUtc = slot.StartTimeUtc,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.PendingBookings.Add(pendingBooking);
        await _db.SaveChangesAsync(ct);

        return new ToolResult(
            Success: true,
            Message: $"تم تجهيز مسودة الحجز بنجاح بانتظار تأكيد العميل الصريح. بيانات الحجز: {serviceName} باسم {customerName}، تليفون {customerPhone}، الموعد: {CairoTimeHelper.FormatCairoFriendly(slot.StartTimeUtc)}.",
            Data: new
            {
                pendingBookingId = pendingBooking.Id,
                customerName,
                customerPhone,
                service = serviceName,
                cairoTime = CairoTimeHelper.FormatCairoFriendly(slot.StartTimeUtc),
                requestHash,
                status = "PendingConfirmation",
                instructionForAgent = "أخبر العميل أنك جهزت تفاصيل الحجز بنجاح، واطلب منه مراجعة بطاقة الحجز المعروضة والضغط على زر تأكيد الحجز لإتمام العملية."
            }
        );
    }
}

public class GetBookingTool : ITool
{
    private readonly AppDbContext _db;

    public GetBookingTool(AppDbContext db)
    {
        _db = db;
    }

    public ToolDefinition Definition => new()
    {
        Name = "GetBooking",
        Description = "الاستعلام عن تفاصيل حجز قائم بالاسم أو برقم التليفون أو برقم الحجز.",
        Parameters = new()
        {
            ["bookingId"] = new("string", "رقم الحجز الفريد إن وجد", Required: false),
            ["customerPhone"] = new("string", "رقم تليفون العميل للبحث عن حجوزاته", Required: false)
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, Guid conversationId, CancellationToken ct)
    {
        Guid? parsedBookingId = null;
        if (arguments.TryGetProperty("bookingId", out var idProp) && Guid.TryParse(idProp.GetString(), out var g))
        {
            parsedBookingId = g;
        }

        string? phone = null;
        if (arguments.TryGetProperty("customerPhone", out var phoneProp))
        {
            phone = phoneProp.GetString();
        }

        var query = _db.Bookings.AsQueryable();

        // Strict ownership: Bind query to the current conversation to prevent cross-conversation disclosure
        if (conversationId != Guid.Empty)
        {
            query = query.Where(b => b.ConversationId == conversationId);
        }

        if (parsedBookingId.HasValue)
        {
            query = query.Where(b => b.Id == parsedBookingId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(phone))
        {
            query = query.Where(b => b.CustomerPhone == phone.Trim());
        }
        else
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "MISSING_SEARCH_CRITERIA",
                Message: "يرجى تقديم رقم الحجز أو رقم تليفون العميل للاستعلام."
            );
        }

        var bookings = await query.OrderByDescending(b => b.CreatedAtUtc).Take(3).ToListAsync(ct);

        if (bookings.Count == 0)
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "NOT_FOUND",
                Message: "لم يتم العثور على أي حجز مطابق للبيانات المدخلة."
            );
        }

        var results = bookings.Select(b => new
        {
            bookingId = b.Id,
            customerName = b.CustomerName,
            customerPhone = b.CustomerPhone,
            service = b.ServiceName,
            cairoTime = CairoTimeHelper.FormatCairoFriendly(b.BookingDateUtc),
            status = b.Status
        }).ToList();

        return new ToolResult(
            Success: true,
            Message: $"تم العثور على {results.Count} حجز.",
            Data: new { bookings = results }
        );
    }
}

public class CancelBookingTool : ITool
{
    private readonly AppDbContext _db;

    public CancelBookingTool(AppDbContext db)
    {
        _db = db;
    }

    public ToolDefinition Definition => new()
    {
        Name = "CancelBooking",
        Description = "إلغاء حجز مؤكد للعميل بالاسم أو برقم التليفون أو برقم الحجز وتحرير الموعد في جدول العيادة. (إلغاء حجز موعد)",
        Parameters = new()
        {
            ["bookingId"] = new("string", "رقم الحجز الفريد إن وجد", Required: false),
            ["customerPhone"] = new("string", "رقم تليفون العميل المسجل به الحجز", Required: false),
            ["reason"] = new("string", "سبب الإلغاء إن ذكره العميل", Required: false)
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, Guid conversationId, CancellationToken ct)
    {
        Guid? parsedBookingId = null;
        if (arguments.TryGetProperty("bookingId", out var idProp) && Guid.TryParse(idProp.GetString(), out var g))
        {
            parsedBookingId = g;
        }

        string? phone = null;
        if (arguments.TryGetProperty("customerPhone", out var phoneProp))
        {
            var raw = phoneProp.GetString();
            phone = EgyptianDateTimeParser.NormalizeEgyptianPhone(raw) ?? raw?.Trim();
        }

        var query = _db.Bookings.Include(b => b.Slot).AsQueryable();

        // Enforce conversation ownership to prevent cross-customer access
        if (conversationId != Guid.Empty)
        {
            query = query.Where(b => b.ConversationId == conversationId);
        }

        if (parsedBookingId.HasValue)
        {
            query = query.Where(b => b.Id == parsedBookingId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(phone))
        {
            query = query.Where(b => b.CustomerPhone == phone);
        }
        else
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "MISSING_SEARCH_CRITERIA",
                Message: "يرجى تقديم رقم الحجز أو رقم تليفون العميل لإلغاء الحجز."
            );
        }

        var booking = await query
            .OrderByDescending(b => b.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (booking == null)
        {
            return new ToolResult(
                Success: false,
                ErrorCode: "BOOKING_NOT_FOUND",
                Message: "لم يتم العثور على حجز نشط مطابق للبيانات في هذه المحادثة."
            );
        }

        if (booking.Status == "Cancelled")
        {
            return new ToolResult(
                Success: true,
                Message: $"هذا الحجز ملغى بالفعل مسبقاً (رقم الحجز: {booking.Id}).",
                Data: new { bookingId = booking.Id, status = "Cancelled" }
            );
        }

        booking.Status = "Cancelled";

        // Free up slot capacity atomically
        if (booking.Slot != null)
        {
            booking.Slot.BookedCapacity = Math.Max(0, booking.Slot.BookedCapacity - 1);
        }
        else
        {
            var slot = await _db.AvailabilitySlots.FirstOrDefaultAsync(s => s.Id == booking.SlotId, ct);
            if (slot != null)
            {
                slot.BookedCapacity = Math.Max(0, slot.BookedCapacity - 1);
            }
        }

        // Atomically enqueue durable OutboxJob inside the same change tracker
        _db.OutboxJobs.Add(new OutboxJob
        {
            Id = Guid.NewGuid(),
            Topic = "BookingCancelled",
            PayloadJson = JsonSerializer.Serialize(new
            {
                bookingId = booking.Id,
                customerName = booking.CustomerName,
                customerPhone = booking.CustomerPhone,
                serviceName = booking.ServiceName,
                bookingDateUtc = booking.BookingDateUtc,
                cancelledAtUtc = DateTime.UtcNow
            }),
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow,
            NextRetryUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        return new ToolResult(
            Success: true,
            Message: $"تم إلغاء حجزك بنجاح يا فندم (حجز {booking.ServiceName} لموعد {CairoTimeHelper.FormatCairoFriendly(booking.BookingDateUtc)}). يسعدنا خدمتك في أي وقت آخر.",
            Data: new
            {
                bookingId = booking.Id,
                customerName = booking.CustomerName,
                status = "Cancelled",
                cairoTime = CairoTimeHelper.FormatCairoFriendly(booking.BookingDateUtc)
            }
        );
    }
}

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools)
        {
            _tools[tool.Definition.Name] = tool;
        }
    }

    public bool HasTool(string name) => _tools.ContainsKey(name);

    public ITool? GetTool(string name)
    {
        _tools.TryGetValue(name, out var tool);
        return tool;
    }

    public IEnumerable<ToolDefinition> GetAllowedDefinitions(IEnumerable<string> allowedNames)
    {
        var set = new HashSet<string>(allowedNames, StringComparer.OrdinalIgnoreCase);
        return _tools.Values
            .Where(t => set.Contains(t.Definition.Name))
            .Select(t => t.Definition);
    }
}
