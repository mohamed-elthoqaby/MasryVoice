namespace MasryVoice.Api.Features.Integrations;

public interface ITelegramMessagingService
{
    string ProviderName { get; }
    bool IsConfigured { get; }
    bool IsMock { get; }
    string? BlockerReason { get; }

    Task<IntegrationDispatchResult> NotifyClinicStaffAsync(string notificationText, CancellationToken ct = default);
    Task<IntegrationDispatchResult> SendMessageAsync(long chatId, string text, CancellationToken ct = default);
    bool VerifyWebhookSecret(string? secretTokenHeader);
}
