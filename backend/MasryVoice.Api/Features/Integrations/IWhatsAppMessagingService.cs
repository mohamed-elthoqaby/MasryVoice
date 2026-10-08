namespace MasryVoice.Api.Features.Integrations;

public interface IWhatsAppMessagingService
{
    string ProviderName { get; }
    bool IsConfigured { get; }
    bool IsMock { get; }
    string? BlockerReason { get; }

    Task<IntegrationDispatchResult> SendBookingConfirmationAsync(
        string toPhoneNumber,
        Guid bookingId,
        string customerName,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default);

    Task<IntegrationDispatchResult> SendBookingReminderAsync(
        string toPhoneNumber,
        Guid bookingId,
        string customerName,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default);

    Task<IntegrationDispatchResult> SendBookingCancellationAsync(
        string toPhoneNumber,
        Guid bookingId,
        string customerName,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default);

    Task<IntegrationDispatchResult> SendCustomMessageAsync(
        string toPhoneNumber,
        string messageText,
        CancellationToken ct = default);

    bool VerifyWebhookSignature(string payload, string? signatureHeader);
}
