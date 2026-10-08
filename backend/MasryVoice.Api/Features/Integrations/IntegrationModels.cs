using System.Text.Json.Serialization;

namespace MasryVoice.Api.Features.Integrations;

public record IntegrationDispatchResult(
    bool Success,
    string? ExternalId,
    string? ErrorMessage,
    DateTime DispatchedAtUtc);

public record CalendarAppointment(
    Guid BookingId,
    string CustomerName,
    string CustomerPhone,
    string ServiceName,
    DateTime BookingDateUtc,
    TimeSpan Duration,
    string? ExternalEventId = null);

public record WhatsAppMessage(
    string ToPhoneNumber,
    string Body,
    string MessageType,
    string? ExternalMessageId = null,
    DateTime SentAtUtc = default);

public record TelegramNotification(
    long ChatId,
    string Text,
    string? Topic = null,
    DateTime SentAtUtc = default);

public record ProviderStatusDto(
    string ProviderName,
    bool IsConfigured,
    bool IsMock,
    string? BlockerReason);

public record IntegrationsStatusResponse(
    string ActiveProviderMode,
    ProviderStatusDto Calendar,
    ProviderStatusDto WhatsApp,
    ProviderStatusDto Telegram,
    IReadOnlyList<string> ExternalBlockers);
