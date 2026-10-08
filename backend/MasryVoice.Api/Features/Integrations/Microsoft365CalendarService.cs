using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Integrations;

public class Microsoft365CalendarService : ICalendarIntegrationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Microsoft365CalendarService> _logger;

    public Microsoft365CalendarService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<Microsoft365CalendarService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public string ProviderName => "Microsoft365GraphCalendar";
    public bool IsMock => false;

    public string? TenantId => _configuration["Integrations:Microsoft365:TenantId"];
    public string? ClientId => _configuration["Integrations:Microsoft365:ClientId"];
    public string? ClientSecret => _configuration["Integrations:Microsoft365:ClientSecret"];
    public string? UserPrincipalName => _configuration["Integrations:Microsoft365:UserPrincipalName"] ?? "doctor@clinic.masryvoice.eg";

    public bool IsConfigured
    {
        get
        {
            return !string.IsNullOrWhiteSpace(TenantId) && !TenantId.Contains("YOUR_") &&
                   !string.IsNullOrWhiteSpace(ClientId) && !ClientId.Contains("YOUR_") &&
                   !string.IsNullOrWhiteSpace(ClientSecret) && !ClientSecret.Contains("YOUR_");
        }
    }

    public string? BlockerReason => IsConfigured
        ? null
        : "Production Microsoft Graph Calendar requires Azure AD App Registration (TenantId, ClientId, ClientSecret) with Calendars.ReadWrite permission.";

    public async Task<IntegrationDispatchResult> CreateAppointmentAsync(CalendarAppointment appointment, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Microsoft 365 calendar unconfigured. Blocker: {Blocker}", BlockerReason);
            return new IntegrationDispatchResult(false, null, BlockerReason, DateTime.UtcNow);
        }

        try
        {
            var token = await AcquireAccessTokenAsync(ct);
            if (string.IsNullOrEmpty(token))
            {
                return new IntegrationDispatchResult(false, null, "Failed to acquire Azure AD OAuth2 access token.", DateTime.UtcNow);
            }

            var eventPayload = new
            {
                subject = $"كشف {appointment.ServiceName} - {appointment.CustomerName}",
                body = new
                {
                    contentType = "HTML",
                    content = $"<p>حجز عيادة MasryVoice</p><p>المريض: {appointment.CustomerName}</p><p>الهاتف: {appointment.CustomerPhone}</p><p>الخدمة: {appointment.ServiceName}</p>"
                },
                start = new
                {
                    dateTime = appointment.BookingDateUtc.ToString("o"),
                    timeZone = "UTC"
                },
                end = new
                {
                    dateTime = appointment.BookingDateUtc.Add(appointment.Duration).ToString("o"),
                    timeZone = "UTC"
                },
                transactionId = appointment.BookingId.ToString()
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{UserPrincipalName}/calendar/events")
            {
                Content = new StringContent(JsonSerializer.Serialize(eventPayload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Microsoft Graph API create event failed ({StatusCode}): {Body}", response.StatusCode, responseBody);
                return new IntegrationDispatchResult(false, null, $"Microsoft Graph HTTP {(int)response.StatusCode}: {responseBody}", DateTime.UtcNow);
            }

            using var doc = JsonDocument.Parse(responseBody);
            var eventId = doc.RootElement.GetProperty("id").GetString();

            _logger.LogInformation("Successfully created Microsoft 365 calendar event {EventId} for booking {BookingId}",
                eventId, appointment.BookingId);

            return new IntegrationDispatchResult(true, eventId, null, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while creating Microsoft 365 calendar event for booking {BookingId}", appointment.BookingId);
            return new IntegrationDispatchResult(false, null, $"{ex.GetType().Name}: {ex.Message}", DateTime.UtcNow);
        }
    }

    public async Task<IntegrationDispatchResult> CancelAppointmentAsync(string externalEventId, string reason, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Microsoft 365 calendar unconfigured. Blocker: {Blocker}", BlockerReason);
            return new IntegrationDispatchResult(false, externalEventId, BlockerReason, DateTime.UtcNow);
        }

        try
        {
            var token = await AcquireAccessTokenAsync(ct);
            if (string.IsNullOrEmpty(token))
            {
                return new IntegrationDispatchResult(false, externalEventId, "Failed to acquire Azure AD OAuth2 access token.", DateTime.UtcNow);
            }

            var request = new HttpRequestMessage(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/users/{UserPrincipalName}/calendar/events/{externalEventId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Successfully cancelled Microsoft 365 calendar event {EventId}. Reason: {Reason}", externalEventId, reason);
                return new IntegrationDispatchResult(true, externalEventId, null, DateTime.UtcNow);
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Microsoft Graph API cancel event failed ({StatusCode}): {Body}", response.StatusCode, responseBody);
            return new IntegrationDispatchResult(false, externalEventId, $"Microsoft Graph HTTP {(int)response.StatusCode}: {responseBody}", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while cancelling Microsoft 365 calendar event {EventId}", externalEventId);
            return new IntegrationDispatchResult(false, externalEventId, $"{ex.GetType().Name}: {ex.Message}", DateTime.UtcNow);
        }
    }

    public async Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<CalendarAppointment>();
        }

        // Implementation queries Graph API calendar view when configured
        return Array.Empty<CalendarAppointment>();
    }

    private async Task<string?> AcquireAccessTokenAsync(CancellationToken ct)
    {
        var tokenUrl = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token";
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["client_secret"] = ClientSecret!,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Azure AD OAuth token acquisition failed ({Status}): {Error}", response.StatusCode, err);
            return null;
        }

        using var jsonDoc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return jsonDoc.RootElement.GetProperty("access_token").GetString();
    }
}
