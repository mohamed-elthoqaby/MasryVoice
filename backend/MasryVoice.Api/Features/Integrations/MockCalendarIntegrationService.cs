using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Integrations;

public class MockCalendarIntegrationService : ICalendarIntegrationService
{
    private readonly ConcurrentDictionary<string, CalendarAppointment> _appointments = new();
    private readonly ILogger<MockCalendarIntegrationService> _logger;

    public MockCalendarIntegrationService(ILogger<MockCalendarIntegrationService> logger)
    {
        _logger = logger;
    }

    public string ProviderName => "MockCalendarService";
    public bool IsConfigured => true;
    public bool IsMock => true;
    public string? BlockerReason => null;

    /// <summary>
    /// For test verification: simulate failure to verify outbox retry & dead-letter queue.
    /// </summary>
    public bool ShouldSimulateFailure { get; set; }

    public IReadOnlyCollection<CalendarAppointment> StoredAppointments => _appointments.Values.ToList();

    public Task<IntegrationDispatchResult> CreateAppointmentAsync(CalendarAppointment appointment, CancellationToken ct = default)
    {
        if (ShouldSimulateFailure)
        {
            _logger.LogWarning("MockCalendarService simulating dispatch failure for booking {BookingId}", appointment.BookingId);
            return Task.FromResult(new IntegrationDispatchResult(false, null, "Simulated Calendar API network error", DateTime.UtcNow));
        }

        var externalId = $"m365-mock-{appointment.BookingId}";
        var stored = appointment with { ExternalEventId = externalId };
        _appointments[externalId] = stored;

        _logger.LogInformation("MockCalendarService created appointment for booking {BookingId} (ExternalId: {ExternalId})",
            appointment.BookingId, externalId);

        return Task.FromResult(new IntegrationDispatchResult(true, externalId, null, DateTime.UtcNow));
    }

    public Task<IntegrationDispatchResult> CancelAppointmentAsync(string externalEventId, string reason, CancellationToken ct = default)
    {
        if (ShouldSimulateFailure)
        {
            _logger.LogWarning("MockCalendarService simulating cancellation failure for event {ExternalId}", externalEventId);
            return Task.FromResult(new IntegrationDispatchResult(false, externalEventId, "Simulated Calendar API network error", DateTime.UtcNow));
        }

        if (_appointments.TryRemove(externalEventId, out var removed))
        {
            _logger.LogInformation("MockCalendarService cancelled appointment {ExternalId} for booking {BookingId}. Reason: {Reason}",
                externalEventId, removed.BookingId, reason);
            return Task.FromResult(new IntegrationDispatchResult(true, externalEventId, null, DateTime.UtcNow));
        }

        _logger.LogWarning("MockCalendarService event {ExternalId} not found to cancel; treating as idempotent success.", externalEventId);
        return Task.FromResult(new IntegrationDispatchResult(true, externalEventId, null, DateTime.UtcNow));
    }

    public Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var result = _appointments.Values
            .Where(a => a.BookingDateUtc >= fromUtc && a.BookingDateUtc <= toUtc)
            .OrderBy(a => a.BookingDateUtc)
            .ToList();

        return Task.FromResult<IReadOnlyList<CalendarAppointment>>(result);
    }

    public void Reset()
    {
        _appointments.Clear();
        ShouldSimulateFailure = false;
    }
}
