using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MasryVoice.Api.Common;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Features.Tools;
using MasryVoice.Api.Infrastructure.Persistence;
using Xunit;

namespace MasryVoice.Tests;

public class PhaseBBookingAndDialectTests
{
    private static async Task<(AppDbContext db, SqliteConnection conn)> CreateTestDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.SeedInitialDataAsync();
        return (db, conn);
    }

    [Fact]
    public void EgyptianDateTimeParser_NormalizesArabicDigitsCorrectly()
    {
        var input = "٠١٠١٢٣٤٥٦٧٨";
        var result = EgyptianDateTimeParser.NormalizeDigits(input);
        Assert.Equal("01012345678", result);
    }

    [Theory]
    [InlineData("01012345678", "01012345678")]
    [InlineData("+201012345678", "01012345678")]
    [InlineData("00201012345678", "01012345678")]
    [InlineData("011 9876 5432", "01198765432")]
    [InlineData("012-3456-7890", "01234567890")]
    [InlineData("٠١٥١٢٣٤٥٦٧٨", "01512345678")]
    [InlineData("1012345678", "01012345678")]
    public void EgyptianDateTimeParser_NormalizesValidPhones(string input, string expected)
    {
        var result = EgyptianDateTimeParser.NormalizeEgyptianPhone(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("01312345678")] // 013 is not a mobile prefix in Egypt
    [InlineData("01012345")]
    [InlineData("abcdefghijk")]
    public void EgyptianDateTimeParser_RejectsInvalidPhones(string input)
    {
        var result = EgyptianDateTimeParser.NormalizeEgyptianPhone(input);
        Assert.Null(result);
    }

    [Fact]
    public void EgyptianDateTimeParser_ParsesRelativeDates()
    {
        var refDate = new DateTime(2026, 10, 10, 12, 0, 0); // Saturday

        Assert.True(EgyptianDateTimeParser.TryParseEgyptianDate("النهاردة", refDate, out var today));
        Assert.Equal(new DateTime(2026, 10, 10), today);

        Assert.True(EgyptianDateTimeParser.TryParseEgyptianDate("بكرة", refDate, out var tomorrow));
        Assert.Equal(new DateTime(2026, 10, 11), tomorrow);

        Assert.True(EgyptianDateTimeParser.TryParseEgyptianDate("بعد بكرة", refDate, out var afterTomorrow));
        Assert.Equal(new DateTime(2026, 10, 12), afterTomorrow);

        Assert.True(EgyptianDateTimeParser.TryParseEgyptianDate("يوم التلات", refDate, out var tuesday));
        Assert.Equal(DayOfWeek.Tuesday, tuesday.DayOfWeek);
    }

    [Fact]
    public async Task CheckAvailabilityTool_FiltersByEgyptianDateAndSpecialty()
    {
        var (db, conn) = await CreateTestDbAsync();
        try
        {
            var nowCairo = CairoTimeHelper.NowCairo;
            var tomorrowUtc = CairoTimeHelper.CairoToUtc(nowCairo.Date.AddDays(1).AddHours(10));

            db.AvailabilitySlots.Add(new AvailabilitySlot
            {
                Id = Guid.NewGuid(),
                ServiceName = "كشف باطنة عامة",
                StartTimeUtc = tomorrowUtc,
                EndTimeUtc = tomorrowUtc.AddMinutes(30),
                TotalCapacity = 5,
                BookedCapacity = 0
            });
            db.AvailabilitySlots.Add(new AvailabilitySlot
            {
                Id = Guid.NewGuid(),
                ServiceName = "كشف قلب وأوعية دموية",
                StartTimeUtc = tomorrowUtc,
                EndTimeUtc = tomorrowUtc.AddMinutes(30),
                TotalCapacity = 5,
                BookedCapacity = 0
            });
            await db.SaveChangesAsync();

            var tool = new CheckAvailabilityTool(db);
            var args = JsonDocument.Parse("{\"date\":\"بكرة\",\"service\":\"باطنة\"}").RootElement;
            var result = await tool.ExecuteAsync(args, Guid.NewGuid(), default);

            Assert.True(result.Success);
            var json = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            Assert.Contains("كشف باطنة عامة", json);
            Assert.DoesNotContain("كشف قلب وأوعية دموية", json);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task StageBookingTool_ValidatesAndNormalizesEgyptianPhone()
    {
        var (db, conn) = await CreateTestDbAsync();
        try
        {
            var agent = await db.Agents.FirstAsync();
            var convId = Guid.NewGuid();
            db.Conversations.Add(new Conversation { Id = convId, AgentId = agent.Id });

            var nowCairo = CairoTimeHelper.NowCairo;
            var slotUtc = CairoTimeHelper.CairoToUtc(nowCairo.Date.AddDays(1).AddHours(11));

            var slot = new AvailabilitySlot
            {
                Id = Guid.NewGuid(),
                ServiceName = "كشف باطنة",
                StartTimeUtc = slotUtc,
                EndTimeUtc = slotUtc.AddMinutes(30),
                TotalCapacity = 3,
                BookedCapacity = 0
            };
            db.AvailabilitySlots.Add(slot);
            await db.SaveChangesAsync();

            var tool = new StageBookingTool(db);

            // 1. Invalid phone rejected
            var invalidPhoneArgs = JsonDocument.Parse(
                JsonSerializer.Serialize(new { customerName = "أحمد محمود", customerPhone = "01999999999", slotId = slot.Id })
            ).RootElement;
            var failResult = await tool.ExecuteAsync(invalidPhoneArgs, convId, default);
            Assert.False(failResult.Success);
            Assert.Equal("INVALID_PHONE_NUMBER", failResult.ErrorCode);

            // 2. Valid phone normalized
            var validPhoneArgs = JsonDocument.Parse(
                JsonSerializer.Serialize(new { customerName = "أحمد محمود", customerPhone = "+20 101 234 5678", slotId = slot.Id })
            ).RootElement;
            var passResult = await tool.ExecuteAsync(validPhoneArgs, convId, default);
            Assert.True(passResult.Success);

            var pending = await db.PendingBookings.FirstOrDefaultAsync(pb => pb.ConversationId == convId);
            Assert.NotNull(pending);
            Assert.Equal("01012345678", pending.CustomerPhone);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelBookingTool_FreesCapacityAndEnqueuesOutboxJob()
    {
        var (db, conn) = await CreateTestDbAsync();
        try
        {
            var agent = await db.Agents.FirstAsync();
            var convId = Guid.NewGuid();
            db.Conversations.Add(new Conversation { Id = convId, AgentId = agent.Id });

            var slot = new AvailabilitySlot
            {
                Id = Guid.NewGuid(),
                ServiceName = "كشف أطفال",
                StartTimeUtc = DateTime.UtcNow.AddDays(1),
                EndTimeUtc = DateTime.UtcNow.AddDays(1).AddMinutes(30),
                TotalCapacity = 5,
                BookedCapacity = 3
            };
            db.AvailabilitySlots.Add(slot);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ConversationId = convId,
                SlotId = slot.Id,
                CustomerName = "مريم علي",
                CustomerPhone = "01123456789",
                ServiceName = "كشف أطفال",
                BookingDateUtc = slot.StartTimeUtc,
                Status = "Confirmed",
                IdempotencyKey = "idemp_test_1",
                RequestHash = "hash1",
                CreatedAtUtc = DateTime.UtcNow
            };
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();

            var tool = new CancelBookingTool(db);
            var args = JsonDocument.Parse(
                JsonSerializer.Serialize(new { bookingId = booking.Id })
            ).RootElement;

            var result = await tool.ExecuteAsync(args, convId, default);

            Assert.True(result.Success);
            Assert.Equal("Cancelled", booking.Status);
            Assert.Equal(2, slot.BookedCapacity); // Reduced from 3 to 2

            var outboxJob = await db.OutboxJobs.FirstOrDefaultAsync(o => o.Topic == "BookingCancelled");
            Assert.NotNull(outboxJob);
            Assert.Contains(booking.Id.ToString(), outboxJob.PayloadJson);

            // Re-cancelling returns idempotent message without double decrementing
            var reResult = await tool.ExecuteAsync(args, convId, default);
            Assert.True(reResult.Success);
            Assert.Equal(2, slot.BookedCapacity);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
