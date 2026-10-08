using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Integrations;

public class MockTelegramMessagingService : ITelegramMessagingService
{
    private readonly ConcurrentQueue<TelegramNotification> _sentNotifications = new();
    private readonly ILogger<MockTelegramMessagingService> _logger;

    public MockTelegramMessagingService(ILogger<MockTelegramMessagingService> logger)
    {
        _logger = logger;
    }

    public string ProviderName => "MockTelegramService";
    public bool IsConfigured => true;
    public bool IsMock => true;
    public string? BlockerReason => null;

    /// <summary>
    /// For test verification: simulate failure to verify outbox retry & dead-letter queue.
    /// </summary>
    public bool ShouldSimulateFailure { get; set; }

    public IReadOnlyCollection<TelegramNotification> SentNotifications => _sentNotifications.ToList();

    public Task<IntegrationDispatchResult> NotifyClinicStaffAsync(string notificationText, CancellationToken ct = default)
    {
        return SendMessageAsync(-1001234567890L, notificationText, ct);
    }

    public Task<IntegrationDispatchResult> SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        if (ShouldSimulateFailure)
        {
            _logger.LogWarning("MockTelegramService simulating failure for ChatId {ChatId}", chatId);
            return Task.FromResult(new IntegrationDispatchResult(false, null, "Simulated Telegram API network error", DateTime.UtcNow));
        }

        var notification = new TelegramNotification(chatId, text, null, DateTime.UtcNow);
        _sentNotifications.Enqueue(notification);

        _logger.LogInformation("MockTelegramService dispatched message to chat {ChatId}: {Text}", chatId, text);
        return Task.FromResult(new IntegrationDispatchResult(true, $"tg-mock-{Guid.NewGuid():N}", null, DateTime.UtcNow));
    }

    public bool VerifyWebhookSecret(string? secretTokenHeader)
    {
        if (string.IsNullOrWhiteSpace(secretTokenHeader)) return false;
        return secretTokenHeader == "mock-telegram-secret" || secretTokenHeader.Length >= 8;
    }

    public void Reset()
    {
        _sentNotifications.Clear();
        ShouldSimulateFailure = false;
    }
}
