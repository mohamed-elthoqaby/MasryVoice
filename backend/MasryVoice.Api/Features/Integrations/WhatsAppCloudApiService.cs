using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MasryVoice.Api.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Integrations;

public class WhatsAppCloudApiService : IWhatsAppMessagingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WhatsAppCloudApiService> _logger;

    public WhatsAppCloudApiService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<WhatsAppCloudApiService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public string ProviderName => "MetaWhatsAppCloudApi";
    public bool IsMock => false;

    public string? PhoneNumberId => _configuration["Integrations:WhatsApp:PhoneNumberId"];
    public string? AccessToken => _configuration["Integrations:WhatsApp:AccessToken"];
    public string? AppSecret => _configuration["Integrations:WhatsApp:AppSecret"];
    public string? VerifyToken => _configuration["Integrations:WhatsApp:VerifyToken"];

    public bool IsConfigured
    {
        get
        {
            return !string.IsNullOrWhiteSpace(PhoneNumberId) && !PhoneNumberId.Contains("YOUR_") &&
                   !string.IsNullOrWhiteSpace(AccessToken) && !AccessToken.Contains("YOUR_") &&
                   !string.IsNullOrWhiteSpace(AppSecret) && !AppSecret.Contains("YOUR_");
        }
    }

    public string? BlockerReason => IsConfigured
        ? null
        : "Production WhatsApp Cloud API requires Meta Business Account, PhoneNumberId, System User AccessToken, and AppSecret.";

    public async Task<IntegrationDispatchResult> SendBookingConfirmationAsync(
        string toPhoneNumber,
        Guid bookingId,
        string customerName,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default)
    {
        var text = $"أهلاً بحضرتك يا {customerName}، تم تأكيد حجزك في عيادتنا لـ {serviceName} يوم {bookingDateUtc:yyyy-MM-dd HH:mm UTC}. كود الحجز: {bookingId.ToString()[..8]}. مستنيينك تشرفنا!";
        return await SendMessagePayloadAsync(toPhoneNumber, text, ct);
    }

    public async Task<IntegrationDispatchResult> SendBookingReminderAsync(
        string toPhoneNumber,
        Guid bookingId,
        string customerName,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default)
    {
        var text = $"تذكير بميعادك يا {customerName}: حجزك لـ {serviceName} غداً {bookingDateUtc:yyyy-MM-dd HH:mm UTC}. برجاء الحضور قبل الميعاد بـ 10 دقائق.";
        return await SendMessagePayloadAsync(toPhoneNumber, text, ct);
    }

    public async Task<IntegrationDispatchResult> SendBookingCancellationAsync(
        string toPhoneNumber,
        Guid bookingId,
        string customerName,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default)
    {
        var text = $"أهلاً بحضرتك يا {customerName}، بنأكد لحضرتك إنه تم إلغاء حجز {serviceName} ({bookingDateUtc:yyyy-MM-dd HH:mm UTC}). نشكرك للتواصل معنا.";
        return await SendMessagePayloadAsync(toPhoneNumber, text, ct);
    }

    public async Task<IntegrationDispatchResult> SendCustomMessageAsync(
        string toPhoneNumber,
        string messageText,
        CancellationToken ct = default)
    {
        return await SendMessagePayloadAsync(toPhoneNumber, messageText, ct);
    }

    public bool VerifyWebhookSignature(string payload, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(AppSecret))
            return false;

        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedHex = signatureHeader[prefix.Length..];

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AppSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var computedHex = Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedHex.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(computedHex));
    }

    private async Task<IntegrationDispatchResult> SendMessagePayloadAsync(string recipientPhone, string messageBody, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("WhatsApp Cloud API unconfigured. Blocker: {Blocker}", BlockerReason);
            return new IntegrationDispatchResult(false, null, BlockerReason, DateTime.UtcNow);
        }

        try
        {
            // Normalize Egyptian local format (01xxxxxxxxx) to international E.164 (+201xxxxxxxxx)
            var normalizedPhone = EgyptianDateTimeParser.NormalizeEgyptianPhone(recipientPhone) ?? recipientPhone;
            var internationalPhone = normalizedPhone.StartsWith("01") ? "2" + normalizedPhone : normalizedPhone;

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = internationalPhone,
                type = "text",
                text = new { preview_url = false, body = messageBody }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/v19.0/{PhoneNumberId}/messages")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("WhatsApp Cloud API send failed ({StatusCode}): {Body}", response.StatusCode, responseBody);
                return new IntegrationDispatchResult(false, null, $"Meta Graph HTTP {(int)response.StatusCode}: {responseBody}", DateTime.UtcNow);
            }

            using var doc = JsonDocument.Parse(responseBody);
            var messageId = doc.RootElement.GetProperty("messages")[0].GetProperty("id").GetString();

            _logger.LogInformation("WhatsApp Cloud API message sent to {Phone}. MsgId: {MsgId}", internationalPhone, messageId);
            return new IntegrationDispatchResult(true, messageId, null, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending WhatsApp message to {Recipient}", recipientPhone);
            return new IntegrationDispatchResult(false, null, $"{ex.GetType().Name}: {ex.Message}", DateTime.UtcNow);
        }
    }
}
