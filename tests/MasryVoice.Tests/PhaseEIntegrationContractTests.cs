using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MasryVoice.Api.Common;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Features.Automation;
using MasryVoice.Api.Features.Integrations;
using MasryVoice.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MasryVoice.Tests;

public class PhaseEIntegrationContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public PhaseEIntegrationContractTests(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Integrations:ProviderMode"] = "Mock",
                    ["Integrations:WhatsApp:VerifyToken"] = "test-wa-verify-token",
                    ["Integrations:Telegram:WebhookSecretToken"] = "test-tg-secret-token"
                });
            });
        });
        _output = output;
    }

    [Fact]
    public async Task Calendar_Mock_Creates_Cancels_And_Queries_Appointments()
    {
        var mockCalendar = new MockCalendarIntegrationService(NullLogger<MockCalendarIntegrationService>.Instance);
        var bookingId = Guid.NewGuid();
        var date = DateTime.UtcNow.AddDays(1);
        var appt = new CalendarAppointment(
            bookingId,
            "أحمد محمود",
            "01012345678",
            "كشف باطنة",
            date,
            TimeSpan.FromMinutes(30));

        // Create
        var createRes = await mockCalendar.CreateAppointmentAsync(appt);
        Assert.True(createRes.Success);
        Assert.NotNull(createRes.ExternalId);
        Assert.Contains(bookingId.ToString(), createRes.ExternalId);
        Assert.Single(mockCalendar.StoredAppointments);

        // Query
        var queried = await mockCalendar.GetAppointmentsAsync(date.AddHours(-1), date.AddHours(1));
        Assert.Single(queried);
        Assert.Equal("أحمد محمود", queried[0].CustomerName);

        // Cancel
        var cancelRes = await mockCalendar.CancelAppointmentAsync(createRes.ExternalId, "طلب المريض");
        Assert.True(cancelRes.Success);
        Assert.Empty(mockCalendar.StoredAppointments);

        _output.WriteLine("MockCalendarService contract passed cleanly.");
    }

    [Fact]
    public async Task Calendar_Microsoft365_Unconfigured_Explicitly_Blocks_Without_Fake_Success()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        var httpClient = new HttpClient();
        var m365 = new Microsoft365CalendarService(httpClient, emptyConfig, NullLogger<Microsoft365CalendarService>.Instance);

        Assert.False(m365.IsConfigured);
        Assert.False(m365.IsMock);
        Assert.NotNull(m365.BlockerReason);
        Assert.Contains("Azure AD", m365.BlockerReason);

        var appt = new CalendarAppointment(
            Guid.NewGuid(),
            "محمود علي",
            "01123456789",
            "كشف جلدية",
            DateTime.UtcNow.AddDays(2),
            TimeSpan.FromMinutes(30));

        var result = await m365.CreateAppointmentAsync(appt);
        Assert.False(result.Success);
        Assert.Null(result.ExternalId);
        Assert.Contains("Azure AD", result.ErrorMessage);

        _output.WriteLine("Microsoft365CalendarService unconfigured blocker verified.");
    }

    [Fact]
    public async Task WhatsApp_Mock_Normalizes_Numbers_And_Sends_Colloquial_Messages()
    {
        var mockWa = new MockWhatsAppMessagingService(NullLogger<MockWhatsAppMessagingService>.Instance);
        var bookingId = Guid.NewGuid();
        var date = DateTime.UtcNow.AddDays(1);

        // Confirmation
        var res1 = await mockWa.SendBookingConfirmationAsync("+20 10 1234 5678", bookingId, "سارة", "أسنان", date);
        Assert.True(res1.Success);

        // Reminder
        var res2 = await mockWa.SendBookingReminderAsync("01123456789", bookingId, "سارة", "أسنان", date);
        Assert.True(res2.Success);

        // Cancellation
        var res3 = await mockWa.SendBookingCancellationAsync("01234567890", bookingId, "سارة", "أسنان", date);
        Assert.True(res3.Success);

        var sent = mockWa.SentMessages.ToList();
        Assert.Equal(3, sent.Count);

        var confirmMsg = sent.FirstOrDefault(m => m.MessageType == "BookingConfirmation");
        var reminderMsg = sent.FirstOrDefault(m => m.MessageType == "BookingReminder");
        var cancelMsg = sent.FirstOrDefault(m => m.MessageType == "BookingCancellation");

        Assert.NotNull(confirmMsg);
        Assert.NotNull(reminderMsg);
        Assert.NotNull(cancelMsg);

        Assert.Contains("سارة", confirmMsg.Body);
        Assert.Contains("تأكيد", confirmMsg.Body);
        Assert.Contains("تذكير", reminderMsg.Body);
        Assert.Contains("إلغاء", cancelMsg.Body);

        // Check phone normalization
        Assert.Equal("01012345678", confirmMsg.ToPhoneNumber);

        _output.WriteLine("MockWhatsAppMessagingService contract passed cleanly.");
    }

    [Fact]
    public void WhatsApp_CloudApi_HMAC_Verification_Validates_Signatures()
    {
        var appSecret = "test_meta_app_secret_123";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Integrations:WhatsApp:AppSecret"] = appSecret
            })
            .Build();

        var wa = new WhatsAppCloudApiService(new HttpClient(), config, NullLogger<WhatsAppCloudApiService>.Instance);

        var payload = "{\"entry\":[{\"id\":\"12345\",\"changes\":[]}]}";

        // Compute valid signature
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var validSigHeader = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();

        Assert.True(wa.VerifyWebhookSignature(payload, validSigHeader));
        Assert.False(wa.VerifyWebhookSignature(payload, "sha256=invalidhash"));
        Assert.False(wa.VerifyWebhookSignature(payload, null));
        Assert.False(wa.VerifyWebhookSignature(payload, ""));

        _output.WriteLine("WhatsApp Cloud API HMAC-SHA256 signature verification contract passed.");
    }

    [Fact]
    public async Task Telegram_Mock_And_BotApi_Contract()
    {
        var mockTg = new MockTelegramMessagingService(NullLogger<MockTelegramMessagingService>.Instance);
        var res = await mockTg.NotifyClinicStaffAsync("🏥 إشعار تجريبي للعيادة");
        Assert.True(res.Success);
        Assert.Single(mockTg.SentNotifications);
        Assert.Contains("إشعار تجريبي", mockTg.SentNotifications.First().Text);

        var unconfiguredConfig = new ConfigurationBuilder().Build();
        var botService = new TelegramBotService(new HttpClient(), unconfiguredConfig, NullLogger<TelegramBotService>.Instance);
        Assert.False(botService.IsConfigured);
        Assert.Contains("BotToken", botService.BlockerReason);

        _output.WriteLine("Telegram messaging contracts passed cleanly.");
    }

    [Fact]
    public async Task IntegrationDispatchCoordinator_Dispatches_To_All_Channels()
    {
        var cal = new MockCalendarIntegrationService(NullLogger<MockCalendarIntegrationService>.Instance);
        var wa = new MockWhatsAppMessagingService(NullLogger<MockWhatsAppMessagingService>.Instance);
        var tg = new MockTelegramMessagingService(NullLogger<MockTelegramMessagingService>.Instance);
        var coordinator = new IntegrationDispatchCoordinator(cal, wa, tg, NullLogger<IntegrationDispatchCoordinator>.Instance);

        var bookingId = Guid.NewGuid();
        var date = DateTime.UtcNow.AddDays(1);

        // 1. BookingConfirmed
        await coordinator.DispatchBookingConfirmedAsync(bookingId, "كريم", "01099887766", "عيادة الرمد", date);
        Assert.Single(cal.StoredAppointments);
        Assert.Single(wa.SentMessages);
        Assert.Single(tg.SentNotifications);

        // 2. BookingReminder
        await coordinator.DispatchBookingReminderAsync(bookingId, "كريم", "01099887766", "عيادة الرمد", date);
        Assert.Equal(2, wa.SentMessages.Count);
        Assert.Equal(2, tg.SentNotifications.Count);

        // 3. BookingCancelled
        await coordinator.DispatchBookingCancelledAsync(bookingId, "كريم", "01099887766", "عيادة الرمد", date, "اعتذار");
        Assert.Empty(cal.StoredAppointments);
        Assert.Equal(3, wa.SentMessages.Count);
        Assert.Equal(3, tg.SentNotifications.Count);

        // 4. Status
        var status = coordinator.GetStatus("Mock");
        Assert.Equal("Mock", status.ActiveProviderMode);
        Assert.True(status.Calendar.IsConfigured);
        Assert.True(status.WhatsApp.IsConfigured);
        Assert.True(status.Telegram.IsConfigured);

        _output.WriteLine("IntegrationDispatchCoordinator multi-channel dispatch verified.");
    }

    [Fact]
    public async Task DurableOutboxProcessor_Dispatches_Through_Coordinator_With_Retry_Handling()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;

        using (var initDb = new AppDbContext(options))
        {
            await initDb.Database.EnsureCreatedAsync();
        }

        var cal = new MockCalendarIntegrationService(NullLogger<MockCalendarIntegrationService>.Instance);
        var wa = new MockWhatsAppMessagingService(NullLogger<MockWhatsAppMessagingService>.Instance);
        var tg = new MockTelegramMessagingService(NullLogger<MockTelegramMessagingService>.Instance);
        var coordinator = new IntegrationDispatchCoordinator(cal, wa, tg, NullLogger<IntegrationDispatchCoordinator>.Instance);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(conn));
        services.AddSingleton(coordinator);
        var sp = services.BuildServiceProvider();

        var processor = new DurableOutboxProcessor(sp, NullLogger<DurableOutboxProcessor>.Instance);

        var bookingId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        using (var setupDb = new AppDbContext(options))
        {
            setupDb.OutboxJobs.Add(new OutboxJob
            {
                Id = jobId,
                Topic = "BookingConfirmed",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    bookingId = bookingId,
                    customerName = "مروان طارق",
                    customerPhone = "01011223344",
                    serviceName = "كشف أطفال",
                    bookingDateUtc = DateTime.UtcNow.AddDays(2)
                }),
                Status = "Pending",
                CreatedAtUtc = DateTime.UtcNow,
                NextRetryUtc = DateTime.UtcNow
            });
            await setupDb.SaveChangesAsync();
        }

        // Process jobs cleanly
        var processed = await processor.ProcessPendingJobsAsync(CancellationToken.None);
        Assert.Equal(1, processed);

        using (var verifyDb = new AppDbContext(options))
        {
            var job = await verifyDb.OutboxJobs.FirstAsync(j => j.Id == jobId);
            Assert.Equal("Completed", job.Status);
            Assert.NotNull(job.ProcessedAtUtc);
        }

        Assert.Single(cal.StoredAppointments);
        Assert.Single(wa.SentMessages);
        Assert.Single(tg.SentNotifications);

        // Verify simulated failure & retry backoff
        cal.ShouldSimulateFailure = true;
        var failJobId = Guid.NewGuid();

        using (var setupDb = new AppDbContext(options))
        {
            setupDb.OutboxJobs.Add(new OutboxJob
            {
                Id = failJobId,
                Topic = "BookingConfirmed",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    bookingId = Guid.NewGuid(),
                    customerName = "طارق",
                    customerPhone = "01099887766",
                    serviceName = "باطنة",
                    bookingDateUtc = DateTime.UtcNow.AddDays(1)
                }),
                Status = "Pending",
                CreatedAtUtc = DateTime.UtcNow,
                NextRetryUtc = DateTime.UtcNow,
                RetryCount = 0,
                MaxRetries = 3
            });
            await setupDb.SaveChangesAsync();
        }

        // Processing fails
        var failProcessed = await processor.ProcessPendingJobsAsync(CancellationToken.None);
        Assert.Equal(0, failProcessed);

        using (var verifyDb = new AppDbContext(options))
        {
            var failedJob = await verifyDb.OutboxJobs.FirstAsync(j => j.Id == failJobId);
            Assert.Equal("Pending", failedJob.Status);
            Assert.Equal(1, failedJob.RetryCount);
            Assert.NotNull(failedJob.LastError);
            Assert.True(failedJob.NextRetryUtc > DateTime.UtcNow);
        }

        _output.WriteLine("DurableOutboxProcessor integration and retry backoff verified.");
    }

    [Fact]
    public async Task WebEndpoints_Integrations_Status_And_Webhooks()
    {
        var client = _factory.CreateClient();

        // 1. Status endpoint
        var statusRes = await client.GetAsync("/api/integrations/status");
        Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);
        var statusJson = await statusRes.Content.ReadFromJsonAsync<IntegrationsStatusResponse>();
        Assert.NotNull(statusJson);
        Assert.Equal("Mock", statusJson.ActiveProviderMode);
        Assert.True(statusJson.Calendar.IsConfigured);
        Assert.True(statusJson.WhatsApp.IsConfigured);
        Assert.True(statusJson.Telegram.IsConfigured);

        // 2. WhatsApp Webhook challenge verification (Meta hub challenge)
        var verifyValid = await client.GetAsync("/api/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=test-wa-verify-token&hub.challenge=123456789");
        Assert.Equal(HttpStatusCode.OK, verifyValid.StatusCode);
        var challengeBody = await verifyValid.Content.ReadAsStringAsync();
        Assert.Equal("123456789", challengeBody);

        var verifyInvalid = await client.GetAsync("/api/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong-token&hub.challenge=123456789");
        Assert.Equal(HttpStatusCode.Forbidden, verifyInvalid.StatusCode);

        // 3. WhatsApp Webhook inbound message (with mock signature)
        var waReq = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/whatsapp/webhook")
        {
            Content = new StringContent("{\"entry\":[{\"changes\":[]}]}", Encoding.UTF8, "application/json")
        };
        waReq.Headers.Add("X-Hub-Signature-256", "sha256=mock-valid-signature");
        var waPostRes = await client.SendAsync(waReq);
        Assert.Equal(HttpStatusCode.OK, waPostRes.StatusCode);

        // 4. Telegram Webhook inbound update
        var tgReq = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/telegram/webhook")
        {
            Content = new StringContent("{\"update_id\":1001,\"message\":{}}", Encoding.UTF8, "application/json")
        };
        tgReq.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "test-tg-secret-token");
        var tgPostRes = await client.SendAsync(tgReq);
        Assert.Equal(HttpStatusCode.OK, tgPostRes.StatusCode);

        // 5. Telegram Webhook rejected without secret token
        var tgReqNoSecret = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/telegram/webhook")
        {
            Content = new StringContent("{\"update_id\":1002}", Encoding.UTF8, "application/json")
        };
        var tgPostNoSecretRes = await client.SendAsync(tgReqNoSecret);
        Assert.Equal(HttpStatusCode.Unauthorized, tgPostNoSecretRes.StatusCode);

        _output.WriteLine("WebEndpoints for integrations status and webhooks verified successfully.");
    }
}
