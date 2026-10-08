using System.Collections.Concurrent;
using MasryVoice.Api.Common;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Integrations;

public class MockWhatsAppMessagingService : IWhatsAppMessagingService
{
    private readonly ConcurrentQueue<WhatsAppMessage> _sentMessages = new();
    private readonly ILogger<MockWhatsAppMessagingService> _logger;

    public MockWhatsAppMessagingService(ILogger<MockWhatsAppMessagingService> logger)
    {
        _logger = logger;
    }

    public string ProviderName => "MockWhatsAppService";
    public bool IsConfigured => true;
    public bool IsMock => true;
    public string? BlockerReason => null;

    /// <summary>
    /// For test verification: simulate failure to verify outbox retry & dead-letter queue.
    /// </summary>
    public bool ShouldSimulateFailure { get; set; }

    public IReadOnlyCollection<WhatsAppMessage> SentMessages => _sentMessages.ToList();

    public Task<IntegrationDispatchResult> SendBookingConfirmationAsync(
        string toPhoneNumber,
        Guid bookingId,
        string customerName,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default)
    {
        var normalizedPhone = EgyptianDateTimeParser.NormalizeEgyptianPhone(toPhoneNumber) ?? toPhoneNumber;
        var formattedDate = bookingDateUtc.ToString("yyyy-MM-dd HH:mm UTC");
        var text = $"أهلاً بحضرتك يا {customerName}، تم تأكيد حجزك في عيادتنا لـ {serviceName} يوم {formattedDate}. كود الحجز: {bookingId.ToString()[..8]}. مستنيينك تشرفنا في الميعاد!";

        return SendMessageInternalAsync(normalizedPhone, text, "BookingConfirmation");
    }

    public Task<IntegrationDispatchResult> SendBookingReminderAsync(
        string toPhoneNumber,
        Guid bookingId,
        string customerName,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default)
    {
        var normalizedPhone = EgyptianDateTimeParser.NormalizeEgyptianPhone(toPhoneNumber) ?? toPhoneNumber;
        var formattedDate = bookingDateUtc.ToString("yyyy-MM-dd HH:mm UTC");
        var text = $"تذكير بميعادك يا {customerName}: حجزك لـ {serviceName} غداً {formattedDate}. برجاء الحضور قبل الموعد بـ 10 دقائق لإنهاء إجراءات الدخول.";

        return SendMessageInternalAsync(normalizedPhone, text, "BookingReminder");
    }

    public Task<IntegrationDispatchResult> SendBookingCancellationAsync(
        string toPhoneNumber,
        Guid bookingId,
        string customerName,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default)
    {
        var normalizedPhone = EgyptianDateTimeParser.NormalizeEgyptianPhone(toPhoneNumber) ?? toPhoneNumber;
        var formattedDate = bookingDateUtc.ToString("yyyy-MM-dd HH:mm UTC");
        var text = $"أهلاً بحضرتك يا {customerName}، بنأكد لحضرتك إنه تم إلغاء حجز {serviceName} ({formattedDate}). لو تحب تحجز ميعاد تاني في أي وقت إحنا في خدمتك.";

        return SendMessageInternalAsync(normalizedPhone, text, "BookingCancellation");
    }

    public Task<IntegrationDispatchResult> SendCustomMessageAsync(
        string toPhoneNumber,
        string messageText,
        CancellationToken ct = default)
    {
        var normalizedPhone = EgyptianDateTimeParser.NormalizeEgyptianPhone(toPhoneNumber) ?? toPhoneNumber;
        return SendMessageInternalAsync(normalizedPhone, messageText, "CustomMessage");
    }

    public bool VerifyWebhookSignature(string payload, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;
        // Mock supports "sha256=mock-valid-signature" or test verification
        return signatureHeader.Equals("sha256=mock-valid-signature", StringComparison.OrdinalIgnoreCase) ||
               signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase);
    }

    private Task<IntegrationDispatchResult> SendMessageInternalAsync(string phone, string text, string type)
    {
        if (ShouldSimulateFailure)
        {
            _logger.LogWarning("MockWhatsAppService simulating failure for recipient {Phone}", phone);
            return Task.FromResult(new IntegrationDispatchResult(false, null, "Simulated WhatsApp API failure", DateTime.UtcNow));
        }

        var messageId = $"wa-mock-{Guid.NewGuid():N}";
        var msg = new WhatsAppMessage(phone, text, type, messageId, DateTime.UtcNow);
        _sentMessages.Enqueue(msg);

        _logger.LogInformation("MockWhatsAppService dispatched {Type} to {Phone}. MsgId: {MsgId}",
            type, phone, messageId);

        return Task.FromResult(new IntegrationDispatchResult(true, messageId, null, DateTime.UtcNow));
    }

    public void Reset()
    {
        _sentMessages.Clear();
        ShouldSimulateFailure = false;
    }
}
