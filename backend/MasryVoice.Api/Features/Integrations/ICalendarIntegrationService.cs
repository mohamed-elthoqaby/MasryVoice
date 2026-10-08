namespace MasryVoice.Api.Features.Integrations;

public interface ICalendarIntegrationService
{
    string ProviderName { get; }
    bool IsConfigured { get; }
    bool IsMock { get; }
    string? BlockerReason { get; }

    Task<IntegrationDispatchResult> CreateAppointmentAsync(CalendarAppointment appointment, CancellationToken ct = default);
    Task<IntegrationDispatchResult> CancelAppointmentAsync(string externalEventId, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
