using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Features.Security;
using MasryVoice.Api.Features.Tools;
using MasryVoice.Api.Infrastructure.Persistence;
using Xunit;

namespace MasryVoice.Tests;

/// <summary>
/// End-to-end authorization acceptance tests enforcing strict cross-user resource isolation,
/// genuine token verification, secret enforcement at startup, and database integrity guarantees.
/// </summary>
public class AuthorizationAcceptanceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _adminKey = "ci_test_admin_key_super_secret_99";
    private readonly string _hmacSecret = "ci_test_hmac_secret_at_least_16_chars_long";

    public AuthorizationAcceptanceTests(WebApplicationFactory<Program> factory)
    {
        var dbName = $"auth_acceptance_{Guid.NewGuid():N}.db";
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DatabaseProvider"] = "Sqlite",
                    ["ConnectionStrings:Sqlite"] = $"Data Source={dbName}",
                    ["Database:InitializeSchema"] = "true",
                    ["Database:AutoSeed"] = "true",
                    ["Security:AdminKey"] = _adminKey,
                    ["Security:HmacSecret"] = _hmacSecret,
                    ["LlmProvider"] = "DeterministicFake",
                    ["Voice:SttProvider"] = "Simulated",
                    ["Voice:TtsProvider"] = "Simulated",
                    ["Inference:EmbeddingProvider"] = "Deterministic"
                });
            });
            builder.ConfigureServices(services =>
            {
                var descriptors = services.Where(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                    d.ServiceType == typeof(AppDbContext)).ToList();
                foreach (var d in descriptors) services.Remove(d);

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite($"Data Source={dbName}");
                });
            });
        });
    }

    private record VoiceSessionResponse(Guid sessionId, Guid conversationId, string customerToken);
    private record PendingBookingResponse(Guid id, Guid conversationId, string customerName, string customerPhone, DateTime slotTimeUtc, string serviceName);
    private record StageBookingResponse(Guid pendingBookingId, string customerName, DateTime slotTimeUtc, string serviceName, string status);
    private record ConfirmBookingResponse(Guid bookingId, string message, string status);
    private record BookingDetailsResponse(Guid id, Guid? conversationId, string customerName, string customerPhone, DateTime slotTimeUtc, string serviceName, string status);

    private async Task<(Guid sessionId, Guid conversationId, string customerToken)> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/voice/session", new { conversationId = Guid.Empty });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var data = await response.Content.ReadFromJsonAsync<VoiceSessionResponse>();
        Assert.NotNull(data);
        Assert.NotEqual(Guid.Empty, data.sessionId);
        Assert.NotEqual(Guid.Empty, data.conversationId);
        Assert.False(string.IsNullOrWhiteSpace(data.customerToken));

        return (data.sessionId, data.conversationId, data.customerToken);
    }

    [Fact]
    public async Task Sessions_A_and_B_AccessOwnResources_Successfully()
    {
        var clientA = _factory.CreateClient();
        var clientB = _factory.CreateClient();

        var (sessA, convA, tokenA) = await CreateSessionAsync(clientA);
        var (sessB, convB, tokenB) = await CreateSessionAsync(clientB);

        // Verify independent identifiers
        Assert.NotEqual(convA, convB);
        Assert.NotEqual(tokenA, tokenB);

        // Session A stages and confirms booking
        clientA.DefaultRequestHeaders.Add("X-Customer-Token", tokenA);
        var stageA = await clientA.PostAsJsonAsync("/api/bookings/stage", new
        {
            conversationId = convA,
            customerName = "عميل أ",
            customerPhone = "01011111111",
            slotTimeUtc = DateTime.UtcNow.AddDays(1),
            serviceName = "استشارة عامة"
        });
        Assert.Equal(HttpStatusCode.OK, stageA.StatusCode);
        var stageDataA = await stageA.Content.ReadFromJsonAsync<StageBookingResponse>();
        Assert.NotNull(stageDataA);

        var confirmA = await clientA.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convA,
            pendingBookingId = stageDataA.pendingBookingId
        });
        Assert.Equal(HttpStatusCode.OK, confirmA.StatusCode);
        var confirmDataA = await confirmA.Content.ReadFromJsonAsync<ConfirmBookingResponse>();
        Assert.NotNull(confirmDataA);

        // Session A can retrieve own booking
        var getA = await clientA.GetAsync($"/api/bookings/{confirmDataA.bookingId}");
        Assert.Equal(HttpStatusCode.OK, getA.StatusCode);

        // Session B stages and confirms its own booking
        clientB.DefaultRequestHeaders.Add("X-Customer-Token", tokenB);
        var stageB = await clientB.PostAsJsonAsync("/api/bookings/stage", new
        {
            conversationId = convB,
            customerName = "عميل ب",
            customerPhone = "01022222222",
            slotTimeUtc = DateTime.UtcNow.AddDays(2),
            serviceName = "خدمة ثانية"
        });
        Assert.Equal(HttpStatusCode.OK, stageB.StatusCode);
        var stageDataB = await stageB.Content.ReadFromJsonAsync<StageBookingResponse>();
        Assert.NotNull(stageDataB);

        var confirmB = await clientB.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convB,
            pendingBookingId = stageDataB.pendingBookingId
        });
        Assert.Equal(HttpStatusCode.OK, confirmB.StatusCode);
        var confirmDataB = await confirmB.Content.ReadFromJsonAsync<ConfirmBookingResponse>();
        Assert.NotNull(confirmDataB);

        // Session B can retrieve own booking
        var getB = await clientB.GetAsync($"/api/bookings/{confirmDataB.bookingId}");
        Assert.Equal(HttpStatusCode.OK, getB.StatusCode);
    }

    [Fact]
    public async Task Session_B_Cannot_Read_Modify_Confirm_Resume_Or_Interrupt_Session_A_Resources()
    {
        var clientA = _factory.CreateClient();
        var clientB = _factory.CreateClient();

        var (sessA, convA, tokenA) = await CreateSessionAsync(clientA);
        var (sessB, convB, tokenB) = await CreateSessionAsync(clientB);

        clientA.DefaultRequestHeaders.Add("X-Customer-Token", tokenA);

        // 1. Session A stages a pending booking
        var stageA = await clientA.PostAsJsonAsync("/api/bookings/stage", new
        {
            conversationId = convA,
            customerName = "عميل أ سرّي",
            customerPhone = "01099998888",
            slotTimeUtc = DateTime.UtcNow.AddDays(3),
            serviceName = "فحص خاص"
        });
        Assert.Equal(HttpStatusCode.OK, stageA.StatusCode);
        var stageDataA = await stageA.Content.ReadFromJsonAsync<StageBookingResponse>();
        Assert.NotNull(stageDataA);

        // Prepare client B with token B
        clientB.DefaultRequestHeaders.Add("X-Customer-Token", tokenB);

        // Count pending bookings before cross-access attempts
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var initialPendingCount = await db.PendingBookings.CountAsync();
            var initialBookingCount = await db.Bookings.CountAsync();

            // 2. Cross-read pending booking: B attempts to read A's pending booking -> 403 Forbidden
            var getPendingRes = await clientB.GetAsync($"/api/bookings/pending?conversationId={convA}");
            Assert.Equal(HttpStatusCode.Forbidden, getPendingRes.StatusCode);

            // 3. Cross-modify / stage: B attempts to stage booking under A's conversation -> 403 Forbidden
            var crossStageRes = await clientB.PostAsJsonAsync("/api/bookings/stage", new
            {
                conversationId = convA,
                customerName = "منتحل صفة",
                customerPhone = "01000000000",
                slotTimeUtc = DateTime.UtcNow.AddDays(4),
                serviceName = "اختراق"
            });
            Assert.Equal(HttpStatusCode.Forbidden, crossStageRes.StatusCode);

            // 4. Cross-confirm: B attempts to confirm A's pending booking -> 403 Forbidden
            var crossConfirmRes = await clientB.PostAsJsonAsync("/api/bookings/confirm", new
            {
                conversationId = convA,
                pendingBookingId = stageDataA.pendingBookingId
            });
            Assert.Equal(HttpStatusCode.Forbidden, crossConfirmRes.StatusCode);

            // 5. Cross-resume / chat token minting: B attempts to chat or resume conversation A -> 403 Forbidden
            var crossChatRes = await clientB.PostAsJsonAsync("/api/chat/stream", new
            {
                conversationId = convA,
                message = "مرحباً، هل هذه محادثة العميل أ؟"
            });
            Assert.Equal(HttpStatusCode.Forbidden, crossChatRes.StatusCode);

            // 6. Cross-voice interrupt: B attempts to interrupt A's voice session -> 403 Forbidden
            var crossInterruptRes = await clientB.PostAsJsonAsync("/api/voice/interrupt", new
            {
                sessionId = sessA
            });
            Assert.Equal(HttpStatusCode.Forbidden, crossInterruptRes.StatusCode);

            // 7. Cross-voice turn: B attempts to run a voice turn on A's session -> 403 Forbidden
            var crossTurnRes = await clientB.PostAsJsonAsync("/api/voice/turn", new
            {
                sessionId = sessA,
                conversationId = convA,
                userText = "أمر صوتي مخترق"
            });
            Assert.Equal(HttpStatusCode.Forbidden, crossTurnRes.StatusCode);

            // Verify database state is untouched! Rejected operations leave DB state unchanged
            var pendingCountAfter = await db.PendingBookings.CountAsync();
            var bookingCountAfter = await db.Bookings.CountAsync();
            Assert.Equal(initialPendingCount, pendingCountAfter);
            Assert.Equal(initialBookingCount, bookingCountAfter);
        }

        // Now A confirms legitimately
        var confirmA = await clientA.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convA,
            pendingBookingId = stageDataA.pendingBookingId
        });
        Assert.Equal(HttpStatusCode.OK, confirmA.StatusCode);
        var bookingDataA = await confirmA.Content.ReadFromJsonAsync<ConfirmBookingResponse>();
        Assert.NotNull(bookingDataA);

        // 8. Cross-read confirmed booking: B attempts to read A's booking -> 403 Forbidden
        var crossGetBookingRes = await clientB.GetAsync($"/api/bookings/{bookingDataA.bookingId}");
        Assert.Equal(HttpStatusCode.Forbidden, crossGetBookingRes.StatusCode);
    }

    [Fact]
    public async Task AgentModification_RequiresValidatedAdminAuthorization()
    {
        var client = _factory.CreateClient();

        // 1. Unauthenticated request -> 401 Unauthorized
        var unauthRes = await client.PostAsJsonAsync("/api/agents", new
        {
            name = "وكيل خبيث",
            systemPrompt = "انت عميل خبيث",
            model = "llama3"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        // 2. Request with invalid/wrong admin key -> 401 Unauthorized
        client.DefaultRequestHeaders.Add("Authorization", "Bearer invalid_wrong_admin_token");
        var badAuthRes = await client.PostAsJsonAsync("/api/agents", new
        {
            name = "وكيل غير مصرح",
            systemPrompt = "تجاوز الصلاحيات",
            modelName = "llama3"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, badAuthRes.StatusCode);

        // 3. Request with valid admin key -> 200 OK
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_adminKey}");
        var validRes = await client.PostAsJsonAsync("/api/agents", new
        {
            name = "وكيل إداري معتمد",
            systemPrompt = "أنت مساعد خدمة عملاء",
            modelName = "llama3"
        });
        Assert.Equal(HttpStatusCode.OK, validRes.StatusCode);
    }

    [Fact]
    public async Task PendingBooking_Disclosure_RejectedWithoutToken()
    {
        var client = _factory.CreateClient();
        var targetConvId = Guid.NewGuid();

        // Anonymous/unauthenticated caller attempting to view pending bookings of a conversation
        var response = await client.GetAsync($"/api/bookings/pending?conversationId={targetConvId}");
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChatStream_NeverMintsTokenForExistingConversationWithoutOwnership()
    {
        var clientVictim = _factory.CreateClient();
        var (_, victimConvId, _) = await CreateSessionAsync(clientVictim);

        var attackerClient = _factory.CreateClient();

        // Caller attempts to pass an existing conversationId without token
        var response = await attackerClient.PostAsJsonAsync("/api/chat/stream", new
        {
            conversationId = victimConvId,
            message = "سرقة جلسة المحادثة"
        });

        // Must NOT mint a token and stream; must reject with 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PhoneBased_BookingAccess_DoesNotConferOwnership()
    {
        var clientA = _factory.CreateClient();
        var (sessA, convA, tokenA) = await CreateSessionAsync(clientA);

        clientA.DefaultRequestHeaders.Add("X-Customer-Token", tokenA);
        var stageA = await clientA.PostAsJsonAsync("/api/bookings/stage", new
        {
            conversationId = convA,
            customerName = "عميل برقم هاتف معروف",
            customerPhone = "01055554444",
            slotTimeUtc = DateTime.UtcNow.AddDays(5),
            serviceName = "استشارة"
        });
        var stageDataA = await stageA.Content.ReadFromJsonAsync<StageBookingResponse>();
        Assert.NotNull(stageDataA);

        var confirmA = await clientA.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convA,
            pendingBookingId = stageDataA.pendingBookingId
        });
        var confirmDataA = await confirmA.Content.ReadFromJsonAsync<ConfirmBookingResponse>();
        Assert.NotNull(confirmDataA);

        // Attacker knows the phone number "01055554444" and bookingId, but has NO valid token for conversation A
        var attackerClient = _factory.CreateClient();
        var attackerRes = await attackerClient.GetAsync($"/api/bookings/{confirmDataA.bookingId}");

        // Caller-supplied phone is NOT proof of ownership; must return 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, attackerRes.StatusCode);
    }

    [Fact]
    public async Task LLMTools_GetBookingTool_ScopesQueriesToOwningConversation()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var convA = Guid.NewGuid();
        var convB = Guid.NewGuid();

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            StartTimeUtc = DateTime.UtcNow.AddDays(6),
            EndTimeUtc = DateTime.UtcNow.AddDays(6).AddMinutes(30),
            ServiceName = "خدمة أ",
            TotalCapacity = 5,
            BookedCapacity = 1
        };
        db.AvailabilitySlots.Add(slot);

        var bookingA = new Booking
        {
            Id = Guid.NewGuid(),
            ConversationId = convA,
            SlotId = slot.Id,
            CustomerName = "عميل أ",
            CustomerPhone = "01011112222",
            BookingDateUtc = DateTime.UtcNow.AddDays(6),
            ServiceName = "خدمة أ",
            Status = "Confirmed"
        };
        db.Bookings.Add(bookingA);
        await db.SaveChangesAsync();

        var getBookingTool = scope.ServiceProvider.GetRequiredService<GetBookingTool>();
        var args = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            bookingId = bookingA.Id
        })).RootElement;

        // Querying from Conversation A context succeeds and finds the booking
        var resultA = await getBookingTool.ExecuteAsync(args, convA, CancellationToken.None);
        Assert.True(resultA.Success);
        var jsonOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var dataStrA = JsonSerializer.Serialize(resultA.Data, jsonOptions);
        Assert.Contains(bookingA.CustomerName, dataStrA);
        Assert.Contains(bookingA.ServiceName, dataStrA);

        // Querying from Conversation B context FAILS to find or disclose Booking A
        var resultB = await getBookingTool.ExecuteAsync(args, convB, CancellationToken.None);
        Assert.False(resultB.Success);
        Assert.Contains("لم يتم العثور", resultB.Message);
    }

    private class TestHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "MasryVoice.Api";
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void ProductionSecrets_MissingOrPlaceholder_ThrowsAtStartup()
    {
        var prodEnv = new TestHostEnvironment { EnvironmentName = "Production" };

        // 1. Missing or placeholder AdminKey in Production
        var builderMissingAdmin = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AdminKey"] = "YOUR_ADMIN_KEY_HERE",
                ["Security:HmacSecret"] = "a_very_long_valid_secret_key_123"
            });
        var configMissingAdmin = builderMissingAdmin.Build();

        Assert.Throws<InvalidOperationException>(() => new SecurityService(configMissingAdmin, prodEnv));

        // 2. Missing or placeholder HmacSecret in Production
        var builderMissingHmac = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AdminKey"] = "strong_admin_key_9999",
                ["Security:HmacSecret"] = "placeholder_secret"
            });
        var configMissingHmac = builderMissingHmac.Build();

        Assert.Throws<InvalidOperationException>(() => new SecurityService(configMissingHmac, prodEnv));
    }

    [Fact]
    public void MalformedOrTampered_CustomerTokens_AreStrictlyRejected()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:HmacSecret"] = _hmacSecret
            })
            .Build();
        var security = new SecurityService(config);

        var convId = Guid.NewGuid();
        var validToken = security.GenerateCustomerToken(convId, null);

        // 1. Tampered payload
        var tamperedToken = validToken + "X";
        Assert.False(security.ValidateCustomerAccess(tamperedToken, convId));

        // 2. Null or empty token
        Assert.False(security.ValidateCustomerAccess(null, convId));
        Assert.False(security.ValidateCustomerAccess("", convId));

        // 3. Random non-base64 garbage
        Assert.False(security.ValidateCustomerAccess("not-a-valid-token-format", convId));
    }
}
