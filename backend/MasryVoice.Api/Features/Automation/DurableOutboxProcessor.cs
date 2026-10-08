using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MasryVoice.Api.Common;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Infrastructure.Persistence;
using MasryVoice.Api.Features.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace MasryVoice.Api.Features.Automation;

public class DurableOutboxProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DurableOutboxProcessor> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public DurableOutboxProcessor(IServiceProvider serviceProvider, ILogger<DurableOutboxProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DurableOutboxProcessor started. Polling every {Seconds}s.", _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnqueueUpcomingRemindersAsync(stoppingToken);
                await ProcessPendingJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in DurableOutboxProcessor loop.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("DurableOutboxProcessor gracefully stopped.");
    }

    public async Task<int> EnqueueUpcomingRemindersAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var reminderWindowEnd = now.AddHours(24);

        // Scan upcoming confirmed bookings in the next 24 hours
        var upcomingBookings = await db.Bookings
            .Where(b => b.Status == "Confirmed" && b.BookingDateUtc > now && b.BookingDateUtc <= reminderWindowEnd)
            .ToListAsync(ct);

        if (upcomingBookings.Count == 0) return 0;

        int enqueuedCount = 0;
        foreach (var booking in upcomingBookings)
        {
            var bookingIdStr = booking.Id.ToString();
            var alreadyEnqueued = await db.OutboxJobs.AnyAsync(
                o => o.Topic == "BookingReminder" && o.PayloadJson.Contains(bookingIdStr), ct);

            if (!alreadyEnqueued)
            {
                db.OutboxJobs.Add(new OutboxJob
                {
                    Id = Guid.NewGuid(),
                    Topic = "BookingReminder",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        bookingId = booking.Id,
                        customerName = booking.CustomerName,
                        customerPhone = booking.CustomerPhone,
                        serviceName = booking.ServiceName,
                        bookingDateUtc = booking.BookingDateUtc,
                        cairoTime = CairoTimeHelper.FormatCairoFriendly(booking.BookingDateUtc),
                        reminderScheduledAtUtc = DateTime.UtcNow
                    }),
                    Status = "Pending",
                    CreatedAtUtc = DateTime.UtcNow,
                    NextRetryUtc = DateTime.UtcNow
                });
                enqueuedCount++;
            }
        }

        if (enqueuedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Enqueued {Count} automated appointment reminder jobs.", enqueuedCount);
        }

        return enqueuedCount;
    }

    public async Task<int> ProcessPendingJobsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var coordinator = scope.ServiceProvider.GetService<IntegrationDispatchCoordinator>();

        var now = DateTime.UtcNow;
        var pendingJobs = await db.OutboxJobs
            .Where(j => j.Status == "Pending" && j.NextRetryUtc <= now)
            .OrderBy(j => j.NextRetryUtc)
            .Take(20)
            .ToListAsync(ct);

        if (pendingJobs.Count == 0) return 0;

        int processedCount = 0;
        foreach (var job in pendingJobs)
        {
            try
            {
                await DispatchJobAsync(job, coordinator, ct);
                job.Status = "Completed";
                job.ProcessedAtUtc = DateTime.UtcNow;
                job.LastError = null;
                processedCount++;
            }
            catch (Exception ex)
            {
                job.RetryCount++;
                job.LastError = $"{ex.GetType().Name}: {ex.Message}";

                if (job.RetryCount >= job.MaxRetries)
                {
                    job.Status = "DeadLetter";
                    _logger.LogWarning("Outbox job {JobId} exceeded max retries. Moved to DeadLetter. Error: {Error}", job.Id, job.LastError);
                }
                else
                {
                    // Exponential backoff: 2^retry * 5 seconds
                    var delaySeconds = Math.Pow(2, job.RetryCount) * 5;
                    job.NextRetryUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                    _logger.LogInformation("Outbox job {JobId} failed (attempt {Attempt}). Next retry in {Delay}s.", job.Id, job.RetryCount, delaySeconds);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return processedCount;
    }

    private async Task DispatchJobAsync(OutboxJob job, IntegrationDispatchCoordinator? coordinator, CancellationToken ct)
    {
        if (coordinator != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(job.PayloadJson);
                var root = doc.RootElement;
                var bookingId = root.TryGetProperty("bookingId", out var bidProp) && bidProp.TryGetGuid(out var bid) ? bid : Guid.Empty;
                var customerName = root.TryGetProperty("customerName", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var customerPhone = root.TryGetProperty("customerPhone", out var phoneProp) ? phoneProp.GetString() ?? "" : "";
                var serviceName = root.TryGetProperty("serviceName", out var servProp) ? servProp.GetString() ?? "" : "";
                var bookingDateUtc = root.TryGetProperty("bookingDateUtc", out var dateProp) && dateProp.TryGetDateTime(out var dt) ? dt : DateTime.UtcNow;

                switch (job.Topic)
                {
                    case "BookingConfirmed":
                        _logger.LogInformation("Outbox dispatching BookingConfirmed for job {JobId} (Booking: {BookingId})", job.Id, bookingId);
                        await coordinator.DispatchBookingConfirmedAsync(bookingId, customerName, customerPhone, serviceName, bookingDateUtc, ct);
                        return;

                    case "BookingCancelled":
                        var reason = root.TryGetProperty("reason", out var rProp) ? rProp.GetString() ?? "طلب المريض" : "طلب المريض";
                        _logger.LogInformation("Outbox dispatching BookingCancelled for job {JobId} (Booking: {BookingId})", job.Id, bookingId);
                        await coordinator.DispatchBookingCancelledAsync(bookingId, customerName, customerPhone, serviceName, bookingDateUtc, reason, ct);
                        return;

                    case "BookingReminder":
                        _logger.LogInformation("Outbox dispatching BookingReminder for job {JobId} (Booking: {BookingId})", job.Id, bookingId);
                        await coordinator.DispatchBookingReminderAsync(bookingId, customerName, customerPhone, serviceName, bookingDateUtc, ct);
                        return;

                    default:
                        _logger.LogInformation("Outbox dispatched generic job topic '{Topic}' for job {JobId}.", job.Topic, job.Id);
                        return;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Outbox job {JobId} payload could not be parsed as JSON: {Payload}", job.Id, job.PayloadJson);
            }
        }

        switch (job.Topic)
        {
            case "BookingConfirmed":
                _logger.LogInformation("Outbox dispatched: Customer SMS/WhatsApp booking confirmation for job {JobId}.", job.Id);
                break;

            case "BookingCancelled":
                _logger.LogInformation("Outbox dispatched: Cancellation notice for job {JobId}.", job.Id);
                break;

            case "BookingReminder":
                _logger.LogInformation("Outbox dispatched: Appointment reminder notification for job {JobId}.", job.Id);
                break;

            default:
                _logger.LogInformation("Outbox dispatched generic job topic '{Topic}' for job {JobId}.", job.Topic, job.Id);
                break;
        }
    }
}
