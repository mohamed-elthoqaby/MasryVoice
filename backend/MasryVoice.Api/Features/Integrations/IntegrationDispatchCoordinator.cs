using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Integrations;

public class IntegrationDispatchCoordinator
{
    private readonly ICalendarIntegrationService _calendar;
    private readonly IWhatsAppMessagingService _whatsApp;
    private readonly ITelegramMessagingService _telegram;
    private readonly ILogger<IntegrationDispatchCoordinator> _logger;

    public IntegrationDispatchCoordinator(
        ICalendarIntegrationService calendar,
        IWhatsAppMessagingService whatsApp,
        ITelegramMessagingService telegram,
        ILogger<IntegrationDispatchCoordinator> logger)
    {
        _calendar = calendar;
        _whatsApp = whatsApp;
        _telegram = telegram;
        _logger = logger;
    }

    public ICalendarIntegrationService Calendar => _calendar;
    public IWhatsAppMessagingService WhatsApp => _whatsApp;
    public ITelegramMessagingService Telegram => _telegram;

    public async Task DispatchBookingConfirmedAsync(
        Guid bookingId,
        string customerName,
        string customerPhone,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Dispatching BookingConfirmed across integrations for booking {BookingId}", bookingId);

        // 1. Sync Calendar
        var appointment = new CalendarAppointment(
            bookingId,
            customerName,
            customerPhone,
            serviceName,
            bookingDateUtc,
            TimeSpan.FromMinutes(30));

        var calResult = await _calendar.CreateAppointmentAsync(appointment, ct);
        if (_calendar.IsConfigured && !calResult.Success)
        {
            throw new InvalidOperationException($"Calendar dispatch failed: {calResult.ErrorMessage}");
        }

        // 2. WhatsApp message to patient
        var waResult = await _whatsApp.SendBookingConfirmationAsync(
            customerPhone,
            bookingId,
            customerName,
            serviceName,
            bookingDateUtc,
            ct);
        if (_whatsApp.IsConfigured && !waResult.Success)
        {
            throw new InvalidOperationException($"WhatsApp dispatch failed: {waResult.ErrorMessage}");
        }

        // 3. Telegram notification to clinic staff
        var staffNotice = $"🏥 <b>حجز كشف جديد</b>\n" +
                          $"• المريض: {customerName}\n" +
                          $"• الهاتف: {customerPhone}\n" +
                          $"• التخصص: {serviceName}\n" +
                          $"• الموعد: {bookingDateUtc:yyyy-MM-dd HH:mm UTC}\n" +
                          $"• كود الحجز: <code>{bookingId}</code>";

        var tgResult = await _telegram.NotifyClinicStaffAsync(staffNotice, ct);
        if (_telegram.IsConfigured && !tgResult.Success)
        {
            throw new InvalidOperationException($"Telegram dispatch failed: {tgResult.ErrorMessage}");
        }
    }

    public async Task DispatchBookingCancelledAsync(
        Guid bookingId,
        string customerName,
        string customerPhone,
        string serviceName,
        DateTime bookingDateUtc,
        string reason,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Dispatching BookingCancelled across integrations for booking {BookingId}", bookingId);

        // 1. Calendar cancellation
        var calExternalId = $"m365-mock-{bookingId}";
        await _calendar.CancelAppointmentAsync(calExternalId, reason, ct);

        // 2. WhatsApp cancellation notice to patient
        await _whatsApp.SendBookingCancellationAsync(
            customerPhone,
            bookingId,
            customerName,
            serviceName,
            bookingDateUtc,
            ct);

        // 3. Telegram alert to clinic staff
        var staffNotice = $"⚠️ <b>إلغاء حجز</b>\n" +
                          $"• المريض: {customerName} ({customerPhone})\n" +
                          $"• التخصص: {serviceName}\n" +
                          $"• الموعد الملغي: {bookingDateUtc:yyyy-MM-dd HH:mm UTC}\n" +
                          $"• السبب: {reason}";

        await _telegram.NotifyClinicStaffAsync(staffNotice, ct);
    }

    public async Task DispatchBookingReminderAsync(
        Guid bookingId,
        string customerName,
        string customerPhone,
        string serviceName,
        DateTime bookingDateUtc,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Dispatching BookingReminder across integrations for booking {BookingId}", bookingId);

        // 1. WhatsApp reminder notice to patient
        await _whatsApp.SendBookingReminderAsync(
            customerPhone,
            bookingId,
            customerName,
            serviceName,
            bookingDateUtc,
            ct);

        // 2. Telegram staff notification
        var staffNotice = $"⏰ <b>تذكير موعد تلقائي</b>\n" +
                          $"• المريض: {customerName} ({customerPhone})\n" +
                          $"• التخصص: {serviceName}\n" +
                          $"• الموعد: {bookingDateUtc:yyyy-MM-dd HH:mm UTC}";

        await _telegram.NotifyClinicStaffAsync(staffNotice, ct);
    }

    public IntegrationsStatusResponse GetStatus(string activeProviderMode)
    {
        var externalBlockers = new List<string>();

        if (!_calendar.IsConfigured)
            externalBlockers.Add("Microsoft 365 requires Azure AD App Registration (TenantId, ClientId, ClientSecret).");

        if (!_whatsApp.IsConfigured)
            externalBlockers.Add("WhatsApp requires Meta Business Cloud API (PhoneNumberId, AccessToken, AppSecret).");

        if (!_telegram.IsConfigured)
            externalBlockers.Add("Telegram requires Bot Token from @BotFather and ClinicStaffChatId.");

        return new IntegrationsStatusResponse(
            activeProviderMode,
            new ProviderStatusDto(_calendar.ProviderName, _calendar.IsConfigured, _calendar.IsMock, _calendar.BlockerReason),
            new ProviderStatusDto(_whatsApp.ProviderName, _whatsApp.IsConfigured, _whatsApp.IsMock, _whatsApp.BlockerReason),
            new ProviderStatusDto(_telegram.ProviderName, _telegram.IsConfigured, _telegram.IsMock, _telegram.BlockerReason),
            externalBlockers);
    }
}
