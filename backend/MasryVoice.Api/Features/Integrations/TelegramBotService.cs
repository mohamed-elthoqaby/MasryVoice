using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Integrations;

public class TelegramBotService : ITelegramMessagingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<TelegramBotService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public string ProviderName => "TelegramBotApi";
    public bool IsMock => false;

    public string? BotToken => _configuration["Integrations:Telegram:BotToken"];
    public long ClinicStaffChatId => _configuration.GetValue<long>("Integrations:Telegram:ClinicStaffChatId", 0L);
    public string? WebhookSecretToken => _configuration["Integrations:Telegram:WebhookSecretToken"];

    public bool IsConfigured
    {
        get
        {
            return !string.IsNullOrWhiteSpace(BotToken) && !BotToken.Contains("YOUR_") &&
                   ClinicStaffChatId != 0L;
        }
    }

    public string? BlockerReason => IsConfigured
        ? null
        : "Production Telegram Bot requires BotToken from @BotFather and ClinicStaffChatId (group or admin ID).";

    public async Task<IntegrationDispatchResult> NotifyClinicStaffAsync(string notificationText, CancellationToken ct = default)
    {
        if (ClinicStaffChatId == 0L)
        {
            return new IntegrationDispatchResult(false, null, "Clinic staff chatId not configured in Integrations:Telegram:ClinicStaffChatId", DateTime.UtcNow);
        }

        return await SendMessageAsync(ClinicStaffChatId, notificationText, ct);
    }

    public async Task<IntegrationDispatchResult> SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Telegram bot unconfigured. Blocker: {Blocker}", BlockerReason);
            return new IntegrationDispatchResult(false, null, BlockerReason, DateTime.UtcNow);
        }

        try
        {
            var payload = new
            {
                chat_id = chatId,
                text,
                parse_mode = "HTML"
            };

            var url = $"https://api.telegram.org/bot{BotToken}/sendMessage";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Telegram API send failed ({StatusCode}): {Body}", response.StatusCode, responseBody);
                return new IntegrationDispatchResult(false, null, $"Telegram HTTP {(int)response.StatusCode}: {responseBody}", DateTime.UtcNow);
            }

            using var doc = JsonDocument.Parse(responseBody);
            var messageId = doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt64().ToString();

            _logger.LogInformation("Telegram message dispatched to chat {ChatId}. MsgId: {MsgId}", chatId, messageId);
            return new IntegrationDispatchResult(true, messageId, null, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending Telegram message to {ChatId}", chatId);
            return new IntegrationDispatchResult(false, null, $"{ex.GetType().Name}: {ex.Message}", DateTime.UtcNow);
        }
    }

    public bool VerifyWebhookSecret(string? secretTokenHeader)
    {
        if (string.IsNullOrWhiteSpace(secretTokenHeader) || string.IsNullOrWhiteSpace(WebhookSecretToken))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secretTokenHeader),
            Encoding.UTF8.GetBytes(WebhookSecretToken));
    }
}
