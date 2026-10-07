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
using MasryVoice.Api.Features.Voice;
using MasryVoice.Api.Infrastructure.Persistence;
using MasryVoice.Api.Infrastructure.Providers;
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

                var llmDesc = services.FirstOrDefault(d => d.ServiceType == typeof(ILlmProvider));
                if (llmDesc != null) services.Remove(llmDesc);
                services.AddSingleton<ILlmProvider, DeterministicFakeLlmProvider>();

                var sttDesc = services.FirstOrDefault(d => d.ServiceType == typeof(ISttProvider));
                if (sttDesc != null) services.Remove(sttDesc);
                services.AddSingleton<ISttProvider, SimulatedSttProvider>();

                var ttsDesc = services.FirstOrDefault(d => d.ServiceType == typeof(ITtsProvider));
                if (ttsDesc != null) services.Remove(ttsDesc);
                services.AddSingleton<ITtsProvider, SimulatedTtsProvider>();
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

        var agent = await db.Agents.FirstAsync();
        db.Conversations.AddRange(
            new Conversation { Id = convA, AgentId = agent.Id, Channel = "test", Status = "Active" },
            new Conversation { Id = convB, AgentId = agent.Id, Channel = "test", Status = "Active" }
        );

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

    [Fact]
    public async Task BookingConfirmation_AuthorizationMatrix_EnforcesOwnershipAndLeavesDatabaseUnchanged()
    {
        var client = _factory.CreateClient();

        // 1. Create sessions for Owner A and Owner B
        var (sessA, convA, tokenA) = await CreateSessionAsync(client);
        var (sessB, convB, tokenB) = await CreateSessionAsync(client);

        // 2. Setup an isolated slot in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var security = scope.ServiceProvider.GetRequiredService<ISecurityService>();

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            StartTimeUtc = DateTime.UtcNow.AddDays(10),
            EndTimeUtc = DateTime.UtcNow.AddDays(10).AddMinutes(30),
            ServiceName = "استشارة طبية للمصفوفة",
            TotalCapacity = 5,
            BookedCapacity = 0
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();

        // 3. Stage a pending booking for Owner A
        var clientA = _factory.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-Customer-Token", tokenA);

        var stageRes = await clientA.PostAsJsonAsync("/api/bookings/stage", new
        {
            conversationId = convA,
            customerName = "مريض أ",
            customerPhone = "01099998888",
            slotId = slot.Id,
            serviceName = slot.ServiceName
        });
        Assert.Equal(HttpStatusCode.OK, stageRes.StatusCode);
        var stageData = await stageRes.Content.ReadFromJsonAsync<JsonElement>();
        var pendingId = stageData.GetProperty("pendingBookingId").GetGuid();
        var requestHash = stageData.GetProperty("requestHash").GetString();

        // Helper to verify DB state remains pristine
        async Task AssertDatabaseUnchangedAsync()
        {
            using var checkScope = _factory.Services.CreateScope();
            var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bookingCount = await checkDb.Bookings.CountAsync(b => b.SlotId == slot.Id);
            Assert.Equal(0, bookingCount);

            var dbSlot = await checkDb.AvailabilitySlots.FirstAsync(s => s.Id == slot.Id);
            Assert.Equal(0, dbSlot.BookedCapacity);

            var pending = await checkDb.PendingBookings.FirstAsync(p => p.Id == pendingId);
            Assert.Equal("Pending", pending.Status);

            var outboxCount = await checkDb.OutboxJobs.CountAsync();
            Assert.Equal(0, outboxCount);
        }

        // Generate expired token for Owner A (manually forged with past expiry)
        var expiredPayload = new
        {
            ConversationId = convA,
            PhoneNumber = "01099998888",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(-2)
        };
        var expiredJsonBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expiredPayload));
        var expiredB64 = Convert.ToBase64String(expiredJsonBytes);
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(_hmacSecret));
        var expiredSig = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(expiredB64)));
        var expiredToken = $"{expiredB64}.{expiredSig}";

        // --- TEST CASE 1: Missing Customer Token ---
        var clientMissing = _factory.CreateClient();
        var resMissing = await clientMissing.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convA,
            pendingBookingId = pendingId,
            expectedRequestHash = requestHash
        });
        Assert.Equal(HttpStatusCode.Forbidden, resMissing.StatusCode);
        await AssertDatabaseUnchangedAsync();

        // --- TEST CASE 2: Empty Customer Token ---
        var clientEmpty = _factory.CreateClient();
        clientEmpty.DefaultRequestHeaders.Add("X-Customer-Token", "");
        var resEmpty = await clientEmpty.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convA,
            pendingBookingId = pendingId,
            expectedRequestHash = requestHash
        });
        Assert.Equal(HttpStatusCode.Forbidden, resEmpty.StatusCode);
        await AssertDatabaseUnchangedAsync();

        // --- TEST CASE 3: Malformed Customer Token ---
        var clientMalformed = _factory.CreateClient();
        clientMalformed.DefaultRequestHeaders.Add("X-Customer-Token", "not.a.valid.token");
        var resMalformed = await clientMalformed.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convA,
            pendingBookingId = pendingId,
            expectedRequestHash = requestHash
        });
        Assert.Equal(HttpStatusCode.Forbidden, resMalformed.StatusCode);
        await AssertDatabaseUnchangedAsync();

        // --- TEST CASE 4: Expired Customer Token ---
        var clientExpired = _factory.CreateClient();
        clientExpired.DefaultRequestHeaders.Add("X-Customer-Token", expiredToken);
        var resExpired = await clientExpired.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convA,
            pendingBookingId = pendingId,
            expectedRequestHash = requestHash
        });
        Assert.Equal(HttpStatusCode.Forbidden, resExpired.StatusCode);
        await AssertDatabaseUnchangedAsync();

        // --- TEST CASE 5: Valid Other Owner Token (Token B attempting to confirm Booking A) ---
        var clientOther = _factory.CreateClient();
        clientOther.DefaultRequestHeaders.Add("X-Customer-Token", tokenB);
        var resOther = await clientOther.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convA,
            pendingBookingId = pendingId,
            expectedRequestHash = requestHash
        });
        Assert.Equal(HttpStatusCode.Forbidden, resOther.StatusCode);
        await AssertDatabaseUnchangedAsync();

        // --- TEST CASE 6: Valid Owner Token (Owner A confirms own booking) ---
        var resValid = await clientA.PostAsJsonAsync("/api/bookings/confirm", new
        {
            conversationId = convA,
            pendingBookingId = pendingId,
            expectedRequestHash = requestHash
        });
        Assert.Equal(HttpStatusCode.OK, resValid.StatusCode);

        // Verify successful state transition after genuine owner confirmation
        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var committedBooking = await finalDb.Bookings.FirstOrDefaultAsync(b => b.SlotId == slot.Id);
        Assert.NotNull(committedBooking);
        Assert.Equal(convA, committedBooking.ConversationId);

        var updatedSlot = await finalDb.AvailabilitySlots.FirstAsync(s => s.Id == slot.Id);
        Assert.Equal(1, updatedSlot.BookedCapacity);

        var updatedPending = await finalDb.PendingBookings.FirstAsync(p => p.Id == pendingId);
        Assert.Equal("Confirmed", updatedPending.Status);
    }

    [Fact]
    public async Task VoiceTurn_SessionCrossTalk_TokenB_ConversationB_SessionA_RejectsAndLeavesSessionAActive()
    {
        var client = _factory.CreateClient();

        // 1. Create Session A (Conversation A) and Session B (Conversation B)
        var (sessA, convA, tokenA) = await CreateSessionAsync(client);
        var (sessB, convB, tokenB) = await CreateSessionAsync(client);

        using var scope = _factory.Services.CreateScope();
        var sessionMgr = scope.ServiceProvider.GetRequiredService<MasryVoice.Api.Features.Voice.VoiceSessionManager>();
        var voiceSessionA = sessionMgr.GetSession(sessA);
        Assert.NotNull(voiceSessionA);

        // 2. Start an active turn on Session A
        using var turnA = voiceSessionA.StartNewTurn();
        Assert.True(voiceSessionA.IsTurnActive(turnA.TurnId));
        Assert.False(turnA.Token.IsCancellationRequested);

        // 3. Attacker with Token B and Conversation B attempts to invoke /api/voice/turn targeting Session A
        var attackerClient = _factory.CreateClient();
        attackerClient.DefaultRequestHeaders.Add("X-Customer-Token", tokenB);

        var crossTalkPayload = new
        {
            sessionId = sessA,
            conversationId = convB,
            agentId = Guid.NewGuid(),
            message = "محاولة اختراق جلسة صوتية أ"
        };

        var crossTalkRes = await attackerClient.PostAsJsonAsync("/api/voice/turn", crossTalkPayload);

        // 4. Server MUST reject with 403 Forbidden due to mismatched conversation / ownership
        Assert.Equal(HttpStatusCode.Forbidden, crossTalkRes.StatusCode);

        // 5. CRITICAL: Verify Session A's active turn is completely unaffected and still active!
        Assert.True(voiceSessionA.IsTurnActive(turnA.TurnId));
        Assert.False(turnA.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task NewSessions_RequireServerGeneratedIds_StagingCannotMintAnonymousTokens()
    {
        var client = _factory.CreateClient();

        // 1. Attempting to stage a booking with an arbitrary, uncreated ConversationId MUST be rejected
        var fakeConvId = Guid.NewGuid();
        var unauthenticatedStage = await client.PostAsJsonAsync("/api/bookings/stage", new
        {
            conversationId = fakeConvId,
            customerName = "مهاجم مجهول",
            customerPhone = "01000000000",
            serviceName = "كشف عام"
        });

        // Must reject: Staging does not create conversations or mint unearned tokens
        Assert.Equal(HttpStatusCode.Forbidden, unauthenticatedStage.StatusCode);

        // 2. Proper session creation via /api/sessions issues server-generated identifier and token
        var sessionRes = await client.PostAsync("/api/sessions", null);
        Assert.Equal(HttpStatusCode.OK, sessionRes.StatusCode);

        var sessionData = await sessionRes.Content.ReadFromJsonAsync<JsonElement>();
        var serverConvId = sessionData.GetProperty("conversationId").GetGuid();
        var serverToken = sessionData.GetProperty("customerToken").GetString();

        Assert.NotEqual(Guid.Empty, serverConvId);
        Assert.False(string.IsNullOrWhiteSpace(serverToken));

        // 3. Using the server-issued session allows staging successfully
        var authorizedClient = _factory.CreateClient();
        authorizedClient.DefaultRequestHeaders.Add("X-Customer-Token", serverToken);

        var authorizedStage = await authorizedClient.PostAsJsonAsync("/api/bookings/stage", new
        {
            conversationId = serverConvId,
            customerName = "عميل مصرح",
            customerPhone = "01012345678",
            serviceName = "كشف عام"
        });

        Assert.Equal(HttpStatusCode.OK, authorizedStage.StatusCode);
    }

    [Fact]
    public async Task AdminFixtures_InProductionEnvironment_Returns404NotFound()
    {
        var prodDbName = $"prod_fixtures_{Guid.NewGuid():N}.db";
        var prodAdminKey = "valid_production_admin_key_super_secret_123";
        var prodHmacSecret = "valid_production_hmac_secret_at_least_16_chars_123";

        Environment.SetEnvironmentVariable("Security__AdminKey", prodAdminKey);
        Environment.SetEnvironmentVariable("Security__HmacSecret", prodHmacSecret);
        Environment.SetEnvironmentVariable("DatabaseProvider", "Sqlite");
        Environment.SetEnvironmentVariable("ConnectionStrings__Sqlite", $"Data Source={prodDbName}");

        WebApplicationFactory<Program>? prodFactory = null;
        try
        {
            prodFactory = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    var descriptors = services.Where(d =>
                        d.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                        d.ServiceType == typeof(AppDbContext)).ToList();
                    foreach (var d in descriptors) services.Remove(d);

                    services.AddDbContext<AppDbContext>(options =>
                    {
                        options.UseSqlite($"Data Source={prodDbName}");
                    });
                });
            });

            var client = prodFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Admin-Key", prodAdminKey);

            var res = await client.DeleteAsync($"/api/admin/fixtures?conversationIds={Guid.NewGuid()}");

            // Must be absent in Production (404 Not Found)
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Security__AdminKey", null);
            Environment.SetEnvironmentVariable("Security__HmacSecret", null);
            Environment.SetEnvironmentVariable("DatabaseProvider", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__Sqlite", null);

            prodFactory?.Dispose();
            if (File.Exists(prodDbName))
            {
                try { File.Delete(prodDbName); } catch { }
            }
        }
    }

    [Fact]
    public async Task AdminFixtures_Cleanup_PreservesUnrelatedData_AndRestoresBaselineByIntent()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", _adminKey);

        var fixtureConvId = Guid.NewGuid();
        var unrelatedConvId = Guid.NewGuid();
        var slotId = Guid.NewGuid();

        var fixturePhone = "01011111111";
        var unrelatedPhone = "01099999999";
        var serviceName = "كشف جلدية";

        // 1. Seed slot with unrelated booking + fixture booking
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var agent = await db.Agents.FirstAsync();

            var slot = new AvailabilitySlot
            {
                Id = slotId,
                ServiceName = serviceName,
                StartTimeUtc = DateTime.UtcNow.AddDays(2),
                EndTimeUtc = DateTime.UtcNow.AddDays(2).AddMinutes(30),
                TotalCapacity = 5,
                BookedCapacity = 2
            };
            db.AvailabilitySlots.Add(slot);

            var unrelatedConv = new Conversation
            {
                Id = unrelatedConvId,
                AgentId = agent.Id,
                CustomerName = "عميل مستقل",
                CustomerPhoneNumber = unrelatedPhone,
                Channel = "WebText",
                Status = "Active"
            };
            db.Conversations.Add(unrelatedConv);

            var fixtureConv = new Conversation
            {
                Id = fixtureConvId,
                AgentId = agent.Id,
                CustomerName = "عميل الفكستشر",
                CustomerPhoneNumber = fixturePhone,
                Channel = "WebText",
                Status = "Active"
            };
            db.Conversations.Add(fixtureConv);

            var unrelatedBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ConversationId = unrelatedConvId,
                SlotId = slotId,
                CustomerName = "عميل مستقل",
                CustomerPhone = unrelatedPhone,
                ServiceName = serviceName,
                BookingDateUtc = slot.StartTimeUtc,
                Status = "Confirmed",
                IdempotencyKey = $"unrelated_{Guid.NewGuid():N}",
                RequestHash = "hash_unrelated"
            };
            db.Bookings.Add(unrelatedBooking);

            var fixturePending = new PendingBooking
            {
                Id = Guid.NewGuid(),
                ConversationId = fixtureConvId,
                SlotId = slotId,
                CustomerName = "عميل الفكستشر",
                CustomerPhone = fixturePhone,
                ServiceName = serviceName,
                BookingDateUtc = slot.StartTimeUtc,
                Status = "Confirmed",
                IdempotencyKey = $"fixture_{Guid.NewGuid():N}",
                RequestHash = "hash_fixture"
            };
            db.PendingBookings.Add(fixturePending);

            var fixtureBooking = new Booking
            {
                Id = Guid.NewGuid(),
                ConversationId = fixtureConvId,
                SlotId = slotId,
                CustomerName = "عميل الفكستشر",
                CustomerPhone = fixturePhone,
                ServiceName = serviceName,
                BookingDateUtc = slot.StartTimeUtc,
                Status = "Confirmed",
                IdempotencyKey = fixturePending.IdempotencyKey,
                RequestHash = fixturePending.RequestHash
            };
            db.Bookings.Add(fixtureBooking);

            await db.SaveChangesAsync();
        }

        // 2. Pre-cleanup state checks: verify intent counts
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Count bookings for the fixture booking intent
            var fixtureIntentCountBefore = await db.Bookings.CountAsync(b =>
                b.SlotId == slotId && b.CustomerPhone == fixturePhone && b.ServiceName == serviceName);
            Assert.Equal(1, fixtureIntentCountBefore);

            // Count bookings for unrelated customer
            var unrelatedCountBefore = await db.Bookings.CountAsync(b =>
                b.SlotId == slotId && b.CustomerPhone == unrelatedPhone);
            Assert.Equal(1, unrelatedCountBefore);
        }

        // 3. Execute cleanup targeted strictly at fixture conversation
        var deleteRes = await client.DeleteAsync($"/api/admin/fixtures?conversationIds={fixtureConvId}");
        Assert.Equal(HttpStatusCode.OK, deleteRes.StatusCode);

        // 4. Post-cleanup verification:
        // Assert cleanup restores the fixture baseline (fixture booking intent count is 0)
        // Assert unrelated data is preserved
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var fixtureIntentCountAfter = await db.Bookings.CountAsync(b =>
                b.SlotId == slotId && b.CustomerPhone == fixturePhone && b.ServiceName == serviceName);
            Assert.Equal(0, fixtureIntentCountAfter);

            var unrelatedCountAfter = await db.Bookings.CountAsync(b =>
                b.SlotId == slotId && b.CustomerPhone == unrelatedPhone);
            Assert.Equal(1, unrelatedCountAfter);

            // Verify slot capacity recalculated to match only surviving unrelated active bookings
            var slot = await db.AvailabilitySlots.FirstAsync(s => s.Id == slotId);
            Assert.Equal(1, slot.BookedCapacity);

            // Fixture conversation and pending bookings are cleaned up
            Assert.False(await db.Conversations.AnyAsync(c => c.Id == fixtureConvId));
            Assert.False(await db.PendingBookings.AnyAsync(pb => pb.ConversationId == fixtureConvId));

            // Unrelated conversation survives
            Assert.True(await db.Conversations.AnyAsync(c => c.Id == unrelatedConvId));
        }
    }

    [Fact]
    public async Task VoiceEndpoints_EnforceAuthorization_And_CrossCustomerIsolation()
    {
        var clientA = _factory.CreateClient();
        var clientB = _factory.CreateClient();
        var clientAnon = _factory.CreateClient();

        var (sessA, convA, tokenA) = await CreateSessionAsync(clientA);
        var (sessB, convB, tokenB) = await CreateSessionAsync(clientB);

        // 1. STT: Anonymous request is rejected with 403 Forbidden
        using var emptyContent = new MultipartFormDataContent();
        emptyContent.Add(new ByteArrayContent(new byte[] { 1, 2, 3, 4 }), "file", "audio.wav");
        var sttAnonRes = await clientAnon.PostAsync("/api/voice/stt", emptyContent);
        Assert.Equal(HttpStatusCode.Forbidden, sttAnonRes.StatusCode);

        // 2. STT: Cross-customer token attack (Customer B token attempting to access conversation A) -> 403 Forbidden
        using var crossCustomerContent = new MultipartFormDataContent();
        crossCustomerContent.Add(new ByteArrayContent(new byte[] { 1, 2, 3, 4 }), "file", "audio.wav");
        var crossSttReq = new HttpRequestMessage(HttpMethod.Post, "/api/voice/stt")
        {
            Content = crossCustomerContent
        };
        crossSttReq.Headers.Add("X-Customer-Token", tokenB);
        crossSttReq.Headers.Add("X-Conversation-Id", convA.ToString());
        var crossSttRes = await clientB.SendAsync(crossSttReq);
        Assert.Equal(HttpStatusCode.Forbidden, crossSttRes.StatusCode);

        // 3. STT: Valid customer token for owning conversation -> 200 OK
        using var validContentA = new MultipartFormDataContent();
        validContentA.Add(new ByteArrayContent(new byte[] { 1, 2, 3, 4 }), "file", "audio.wav");
        var validSttReq = new HttpRequestMessage(HttpMethod.Post, "/api/voice/stt")
        {
            Content = validContentA
        };
        validSttReq.Headers.Add("X-Customer-Token", tokenA);
        validSttReq.Headers.Add("X-Conversation-Id", convA.ToString());
        var validSttRes = await clientA.SendAsync(validSttReq);
        Assert.Equal(HttpStatusCode.OK, validSttRes.StatusCode);

        // 4. STT: Valid Admin Key -> 200 OK
        using var adminContent = new MultipartFormDataContent();
        adminContent.Add(new ByteArrayContent(new byte[] { 1, 2, 3, 4 }), "file", "audio.wav");
        var adminSttReq = new HttpRequestMessage(HttpMethod.Post, "/api/voice/stt")
        {
            Content = adminContent
        };
        adminSttReq.Headers.Add("X-Admin-Key", _adminKey);
        var adminSttRes = await clientAnon.SendAsync(adminSttReq);
        Assert.Equal(HttpStatusCode.OK, adminSttRes.StatusCode);

        // 5. TTS: Anonymous request is rejected with 403 Forbidden
        var ttsAnonRes = await clientAnon.PostAsJsonAsync("/api/voice/tts", new { text = "أهلاً بك" });
        Assert.Equal(HttpStatusCode.Forbidden, ttsAnonRes.StatusCode);

        // 6. TTS: Cross-customer token attack -> 403 Forbidden
        var crossTtsReq = new HttpRequestMessage(HttpMethod.Post, "/api/voice/tts")
        {
            Content = JsonContent.Create(new { text = "أهلاً بك", conversationId = convA })
        };
        crossTtsReq.Headers.Add("X-Customer-Token", tokenB);
        var crossTtsRes = await clientB.SendAsync(crossTtsReq);
        Assert.Equal(HttpStatusCode.Forbidden, crossTtsRes.StatusCode);

        // 7. TTS: Valid customer token for owning conversation -> 200 OK with audio/wav
        var validTtsReq = new HttpRequestMessage(HttpMethod.Post, "/api/voice/tts")
        {
            Content = JsonContent.Create(new { text = "أهلاً بك", conversationId = convA })
        };
        validTtsReq.Headers.Add("X-Customer-Token", tokenA);
        var validTtsRes = await clientA.SendAsync(validTtsReq);
        Assert.Equal(HttpStatusCode.OK, validTtsRes.StatusCode);
        Assert.Equal("audio/wav", validTtsRes.Content.Headers.ContentType?.MediaType);

        // 8. TTS: Valid Admin Key -> 200 OK
        var adminTtsReq = new HttpRequestMessage(HttpMethod.Post, "/api/voice/tts")
        {
            Content = JsonContent.Create(new { text = "أهلاً بك" })
        };
        adminTtsReq.Headers.Add("X-Admin-Key", _adminKey);
        var adminTtsRes = await clientAnon.SendAsync(adminTtsReq);
        Assert.Equal(HttpStatusCode.OK, adminTtsRes.StatusCode);
        Assert.Equal("audio/wav", adminTtsRes.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task VoiceEndpoints_Enforce_Size_And_Length_Limits()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", _adminKey);

        // 1. STT rejects file > 10MB with 413 Payload Too Large
        // Simulate oversized stream without allocating 11MB RAM using custom Stream
        var oversizedBytes = new byte[10 * 1024 * 1024 + 1024]; // 10MB + 1KB
        using var oversizedContent = new MultipartFormDataContent();
        oversizedContent.Add(new ByteArrayContent(oversizedBytes), "file", "oversized.wav");
        var sttOversizedRes = await client.PostAsync("/api/voice/stt", oversizedContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, sttOversizedRes.StatusCode);

        // 2. TTS rejects text > 2000 chars with 400 Bad Request
        var longText = new string('ا', 2001);
        var ttsLongRes = await client.PostAsJsonAsync("/api/voice/tts", new { text = longText });
        Assert.Equal(HttpStatusCode.BadRequest, ttsLongRes.StatusCode);

        // 3. TTS rejects empty text with 400 Bad Request
        var ttsEmptyRes = await client.PostAsJsonAsync("/api/voice/tts", new { text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, ttsEmptyRes.StatusCode);
    }

    [Fact]
    public async Task VoiceTurn_WithAudioBase64_ExecutesSequentially_WithoutNestedDeadlock()
    {
        var client = _factory.CreateClient();
        var (sessionId, convId, token) = await CreateSessionAsync(client);
        client.DefaultRequestHeaders.Add("X-Customer-Token", token);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var agent = await db.Agents.FirstAsync(a => a.IsActive);

        // Provide a valid small audio base64 payload (100ms 16kHz PCM WAV)
        using var msDummy = new MemoryStream();
        using (var w = new BinaryWriter(msDummy))
        {
            w.Write("RIFF"u8);
            w.Write(36 + 3200);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1); // PCM
            w.Write((short)1); // Mono
            w.Write(16000);    // SampleRate
            w.Write(32000);    // ByteRate
            w.Write((short)2); // BlockAlign
            w.Write((short)16);// BitsPerSample
            w.Write("data"u8);
            w.Write(3200);     // 100ms
            w.Write(new byte[3200]);
        }
        var dummyAudioBase64 = Convert.ToBase64String(msDummy.ToArray());
        var turnRequest = new
        {
            sessionId = sessionId,
            conversationId = convId,
            agentId = agent.Id,
            audioBase64 = dummyAudioBase64
        };

        var res = await client.PostAsJsonAsync("/api/voice/turn", turnRequest);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("turnId", out var turnIdProp));
        Assert.Equal(1, turnIdProp.GetInt32());
        Assert.True(json.TryGetProperty("text", out var textProp));
        Assert.False(string.IsNullOrWhiteSpace(textProp.GetString()));
        Assert.True(json.TryGetProperty("audioBase64", out var audioBase64Prop));
        Assert.False(string.IsNullOrWhiteSpace(audioBase64Prop.GetString()));
    }

    [Fact]
    public async Task VoiceTurn_Enforces_AudioSize_And_InvalidBase64_And_DurationLimits()
    {
        var client = _factory.CreateClient();
        var (sessionId, convId, token) = await CreateSessionAsync(client);
        client.DefaultRequestHeaders.Add("X-Customer-Token", token);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var agent = await db.Agents.FirstAsync(a => a.IsActive);

        // 1. Invalid Base64 audio -> 400 Bad Request
        var invalidB64Res = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId = agent.Id,
            audioBase64 = "not-valid-base64%%%!!!"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidB64Res.StatusCode);
        var b64Err = await invalidB64Res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_BASE64_AUDIO", b64Err.GetProperty("code").GetString());

        // 2. Truncated audio (< 12 bytes) -> 400 Bad Request
        var truncatedB64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var truncRes = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId = agent.Id,
            audioBase64 = truncatedB64
        });
        Assert.Equal(HttpStatusCode.BadRequest, truncRes.StatusCode);
        var truncErr = await truncRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUDIO_TRUNCATED_OR_CORRUPT", truncErr.GetProperty("code").GetString());

        // 3. Audio duration exceeded (> 30s) -> 400 Bad Request
        // Build valid WAV with byteRate=32000 (16kHz 16-bit Mono) and 35 seconds of ACTUAL sample data (35 * 32000 = 1120000 bytes)
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + 1120000);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)1); // Mono
            writer.Write(16000);    // SampleRate
            writer.Write(32000);    // ByteRate
            writer.Write((short)2); // BlockAlign
            writer.Write((short)16);// BitsPerSample
            writer.Write("data"u8);
            writer.Write(1120000);  // 35 seconds
            writer.Write(new byte[1120000]); // Real 35 seconds of samples
        }
        var overlongB64 = Convert.ToBase64String(ms.ToArray());
        var overlongRes = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId = agent.Id,
            audioBase64 = overlongB64
        });
        Assert.Equal(HttpStatusCode.BadRequest, overlongRes.StatusCode);
        var overlongErr = await overlongRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUDIO_DURATION_EXCEEDED", overlongErr.GetProperty("code").GetString());

        // 3b. Spoofed/truncated WAV header (claims 1120000 bytes but only 64 data bytes present) -> 400 Bad Request
        using var msTrunc = new MemoryStream();
        using (var writer = new BinaryWriter(msTrunc))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + 1120000);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(16000);
            writer.Write(32000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(1120000);
            writer.Write(new byte[64]); // Only 64 bytes -> truncated/spoofed!
        }
        var spoofedB64 = Convert.ToBase64String(msTrunc.ToArray());
        var spoofedRes = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId = agent.Id,
            audioBase64 = spoofedB64
        });
        Assert.Equal(HttpStatusCode.BadRequest, spoofedRes.StatusCode);
        var spoofedErr = await spoofedRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUDIO_TRUNCATED_OR_CORRUPT", spoofedErr.GetProperty("code").GetString());

        // 3c. Arbitrary corrupt payload (neither WAV nor WebM nor OGG) -> 400 Bad Request
        var corruptBytes = new byte[256];
        new Random(42).NextBytes(corruptBytes);
        var corruptB64 = Convert.ToBase64String(corruptBytes);
        var corruptRes = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId = agent.Id,
            audioBase64 = corruptB64
        });
        Assert.Equal(HttpStatusCode.BadRequest, corruptRes.StatusCode);
        var corruptErr = await corruptRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUDIO_TRUNCATED_OR_CORRUPT", corruptErr.GetProperty("code").GetString());

        // 4. Oversized payload (> 10MB) -> 413 Payload Too Large
        var oversizedBytes = new byte[10 * 1024 * 1024 + 1024];
        var oversizedB64 = Convert.ToBase64String(oversizedBytes);
        var oversizedRes = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId = agent.Id,
            audioBase64 = oversizedB64
        });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedRes.StatusCode);
    }

    [Fact]
    public async Task TwoConsecutiveRecordedVoiceTurns_ExecutesSttSeparately_AndPersistsDistinctTranscripts()
    {
        using var client = _factory.CreateClient();
        var (sessionId, convId, custToken) = await CreateSessionAsync(client);
        client.DefaultRequestHeaders.Add("X-Customer-Token", custToken);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var agent = await db.Agents.FirstAsync();

        // Turn 1: 176KB audio (turn1 fixture simulation)
        // Send with a stale message text to prove server prioritizes audio over stale text
        var turn1Audio = new byte[176684];
        // Populate standard valid WAV header so validation passes
        using (var ms1 = new MemoryStream(turn1Audio))
        using (var w1 = new BinaryWriter(ms1))
        {
            w1.Write("RIFF"u8);
            w1.Write(36 + (176684 - 44));
            w1.Write("WAVE"u8);
            w1.Write("fmt "u8);
            w1.Write(16);
            w1.Write((short)1);
            w1.Write((short)1);
            w1.Write(16000);
            w1.Write(32000);
            w1.Write((short)2);
            w1.Write((short)16);
            w1.Write("data"u8);
            w1.Write(176684 - 44);
        }
        var turn1B64 = Convert.ToBase64String(turn1Audio);

        var res1 = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId = agent.Id,
            message = "رسالة قديمة يجب تجاهلها لصالح الصوت",
            audioBase64 = turn1B64,
            mimeType = "audio/wav"
        });
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var doc1 = await res1.Content.ReadFromJsonAsync<JsonElement>();
        var userText1 = doc1.GetProperty("userText").GetString();
        Assert.Contains("المواعيد المتاحة", userText1);

        // Turn 2: 374KB audio (turn2 fixture simulation)
        var turn2Audio = new byte[374828];
        using (var ms2 = new MemoryStream(turn2Audio))
        using (var w2 = new BinaryWriter(ms2))
        {
            w2.Write("RIFF"u8);
            w2.Write(36 + (374828 - 44));
            w2.Write("WAVE"u8);
            w2.Write("fmt "u8);
            w2.Write(16);
            w2.Write((short)1);
            w2.Write((short)1);
            w2.Write(16000);
            w2.Write(32000);
            w2.Write((short)2);
            w2.Write((short)16);
            w2.Write("data"u8);
            w2.Write(374828 - 44);
        }
        var turn2B64 = Convert.ToBase64String(turn2Audio);

        var res2 = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId = agent.Id,
            audioBase64 = turn2B64,
            mimeType = "audio/wav"
        });
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var doc2 = await res2.Content.ReadFromJsonAsync<JsonElement>();
        var userText2 = doc2.GetProperty("userText").GetString();

        // Assert Turn 2 did NOT reuse Turn 1's text and reflects Turn 2's distinct audio transcription
        Assert.NotEqual(userText1, userText2);
        Assert.Contains("محمد عاطف", userText2);

        // Verify database persistence has both distinct user messages
        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbMessages = await db2.Messages
            .Where(m => m.ConversationId == convId && m.Role == "user")
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync();
        Assert.Equal(2, dbMessages.Count);
        Assert.Contains("المواعيد المتاحة", dbMessages[0].Content);
        Assert.Contains("محمد عاطف", dbMessages[1].Content);
    }
}

