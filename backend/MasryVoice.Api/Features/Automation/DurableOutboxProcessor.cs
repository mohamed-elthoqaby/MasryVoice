using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Infrastructure.Persistence;

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

    public async Task<int> ProcessPendingJobsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
                await DispatchJobAsync(job, ct);
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

    private Task DispatchJobAsync(OutboxJob job, CancellationToken ct)
    {
        switch (job.Topic)
        {
            case "BookingConfirmed":
                _logger.LogInformation("Outbox dispatched: Customer SMS/WhatsApp booking confirmation for job {JobId}.", job.Id);
                // Here: integration with SMS provider / Webhook gateway (e.g. Twilio, Infobip, local gateway)
                break;

            case "BookingReminder":
                _logger.LogInformation("Outbox dispatched: Appointment reminder notification for job {JobId}.", job.Id);
                break;

            default:
                _logger.LogInformation("Outbox dispatched generic job topic '{Topic}' for job {JobId}.", job.Topic, job.Id);
                break;
        }

        return Task.CompletedTask;
    }
}
