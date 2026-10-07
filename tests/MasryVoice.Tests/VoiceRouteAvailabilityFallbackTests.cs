using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MasryVoice.Api.Common;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Features.Security;
using MasryVoice.Api.Features.Tools;
using MasryVoice.Api.Features.Voice;
using MasryVoice.Api.Infrastructure.Persistence;
using MasryVoice.Api.Infrastructure.Providers;
using Xunit;

namespace MasryVoice.Tests;

/// <summary>
/// Regression tests verifying the voice turn endpoint (/api/voice/turn) fallback behavior
/// when LLM final response text is empty and tool audit results are saved in the database.
/// Strictly asserts proper parsing of ToolResult envelopes (Data.availableSlots),
/// accurate messaging for alternative dates, handling of empty slots, and isolation from stale previous turns.
/// </summary>
public class VoiceRouteAvailabilityFallbackTests
{
    private class EmptyFinalTextLlmProvider : ILlmProvider
    {
        public string ProviderName => "EmptyFinalTextLlmProvider";

        public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
            LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var lastMessage = request.Messages.LastOrDefault();
            var lastUserMsg = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";

            // If previous step was tool result: return EMPTY final text
            if (lastMessage?.Role == "tool")
            {
                yield return new LlmStreamChunk(DeltaText: "");
                yield return new LlmStreamChunk(IsCompleted: true, FinishReason: "stop");
                yield break;
            }

            // If user asks about appointments: call CheckAvailability
            if (lastUserMsg.Contains("مواعيد") || lastUserMsg.Contains("ميعاد"))
            {
                yield return new LlmStreamChunk(
                    ToolCalls: new List<LlmToolCall>
                    {
                        new($"call_{Guid.NewGuid():N}", "CheckAvailability", "{\"date\":\"tomorrow\",\"service\":\"كشف باطنة عامة\"}")
                    },
                    IsCompleted: true,
                    FinishReason: "tool_calls"
                );
                yield break;
            }

            // Otherwise, empty text
            yield return new LlmStreamChunk(DeltaText: "");
            yield return new LlmStreamChunk(IsCompleted: true, FinishReason: "stop");
        }
    }

    private WebApplicationFactory<Program> CreateTestFactory(string dbName)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DatabaseProvider"] = "Sqlite",
                    ["ConnectionStrings:Sqlite"] = $"Data Source={dbName}",
                    ["Database:InitializeSchema"] = "true",
                    ["Database:AutoSeed"] = "false",
                    ["Security:AdminKey"] = "ci_test_admin_key_super_secret_99",
                    ["Security:HmacSecret"] = "ci_test_hmac_secret_at_least_16_chars_long",
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
                services.AddSingleton<ILlmProvider, EmptyFinalTextLlmProvider>();

                var sttDesc = services.FirstOrDefault(d => d.ServiceType == typeof(ISttProvider));
                if (sttDesc != null) services.Remove(sttDesc);
                services.AddSingleton<ISttProvider, SimulatedSttProvider>();

                var ttsDesc = services.FirstOrDefault(d => d.ServiceType == typeof(ITtsProvider));
                if (ttsDesc != null) services.Remove(ttsDesc);
                services.AddSingleton<ITtsProvider, SimulatedTtsProvider>();
            });
        });
    }

    [Fact]
    public async Task VoiceTurn_WhenLlmFinalTextEmpty_AndCurrentTurnHasAvailableSlots_OffersBookingOnAvailableSlots()
    {
        var dbName = $"fallback_test_{Guid.NewGuid():N}.db";
        using var factory = CreateTestFactory(dbName);
        var client = factory.CreateClient();

        Guid convId;
        Guid agentId;
        string customerToken;

        // Seed agent, conversation, and availability slot for tomorrow
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();

            var agent = new Agent
            {
                Id = Guid.NewGuid(),
                Name = "سارة",
                SystemPrompt = "أنت موظفة استقبال في عيادة النور التخصصية بالقاهرة.",
                AllowedToolsJson = "[\"CheckAvailability\"]"
            };
            db.Agents.Add(agent);

            var conv = new Conversation
            {
                Id = Guid.NewGuid(),
                AgentId = agent.Id,
                CustomerPhoneNumber = "01012345678",
                Channel = "Voice",
                StartedAtUtc = DateTime.UtcNow
            };
            db.Conversations.Add(conv);

            // Tomorrow Cairo time
            var tomorrowCairo = CairoTimeHelper.NowCairo.Date.AddDays(1);
            var slotStartUtc = CairoTimeHelper.CairoToUtc(tomorrowCairo.AddHours(10));
            var slot = new AvailabilitySlot
            {
                Id = Guid.NewGuid(),
                ServiceName = "كشف باطنة عامة",
                StartTimeUtc = slotStartUtc,
                EndTimeUtc = slotStartUtc.AddMinutes(30),
                TotalCapacity = 5,
                BookedCapacity = 0
            };
            db.AvailabilitySlots.Add(slot);
            await db.SaveChangesAsync();

            convId = conv.Id;
            agentId = agent.Id;

            var sec = scope.ServiceProvider.GetRequiredService<ISecurityService>();
            customerToken = sec.GenerateCustomerToken(conv.Id, null);
        }

        // Initialize voice session
        client.DefaultRequestHeaders.Add("X-Customer-Token", customerToken);
        var sessRes = await client.PostAsJsonAsync("/api/voice/session", new { conversationId = convId });
        var sessDoc = await sessRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = sessDoc.GetProperty("sessionId").GetString();

        // Send turn asking for availability
        var turnRes = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId,
            message = "عايز أعرف المواعيد المتاحة بكره",
            mimeType = "audio/wav"
        });

        Assert.Equal(HttpStatusCode.OK, turnRes.StatusCode);
        var turnDoc = await turnRes.Content.ReadFromJsonAsync<JsonElement>();
        var replyText = turnDoc.GetProperty("text").GetString();

        Assert.NotNull(replyText);
        // Must recognize available slots and NOT claim that no slots exist
        Assert.DoesNotContain("لا توجد مواعيد متاحة حالياً", replyText);
        Assert.Contains("تم العثور على", replyText);
        Assert.Contains("تحب أحجز لحضرتك ميعاد في المواعيد المتاحة؟", replyText);
    }

    [Fact]
    public async Task VoiceTurn_WhenLlmFinalTextEmpty_AndAlternativeDatesReturned_PreservesAlternativeDatesMessage()
    {
        var dbName = $"fallback_alt_test_{Guid.NewGuid():N}.db";
        using var factory = CreateTestFactory(dbName);
        var client = factory.CreateClient();

        Guid convId;
        Guid agentId;
        string customerToken;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();

            var agent = new Agent
            {
                Id = Guid.NewGuid(),
                Name = "سارة",
                SystemPrompt = "عيادة النور",
                AllowedToolsJson = "[\"CheckAvailability\"]"
            };
            db.Agents.Add(agent);

            var conv = new Conversation
            {
                Id = Guid.NewGuid(),
                AgentId = agent.Id,
                CustomerPhoneNumber = "01099998888",
                Channel = "Voice",
                StartedAtUtc = DateTime.UtcNow
            };
            db.Conversations.Add(conv);

            // NO slots for tomorrow. Only slots in 3 days (alternatives)
            var dayInThreeDaysCairo = CairoTimeHelper.NowCairo.Date.AddDays(3);
            var altSlotUtc = CairoTimeHelper.CairoToUtc(dayInThreeDaysCairo.AddHours(11));
            var slot = new AvailabilitySlot
            {
                Id = Guid.NewGuid(),
                ServiceName = "كشف باطنة عامة",
                StartTimeUtc = altSlotUtc,
                EndTimeUtc = altSlotUtc.AddMinutes(30),
                TotalCapacity = 3,
                BookedCapacity = 0
            };
            db.AvailabilitySlots.Add(slot);
            await db.SaveChangesAsync();

            convId = conv.Id;
            agentId = agent.Id;

            var sec = scope.ServiceProvider.GetRequiredService<ISecurityService>();
            customerToken = sec.GenerateCustomerToken(conv.Id, null);
        }

        client.DefaultRequestHeaders.Add("X-Customer-Token", customerToken);
        var sessRes = await client.PostAsJsonAsync("/api/voice/session", new { conversationId = convId });
        var sessDoc = await sessRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = sessDoc.GetProperty("sessionId").GetString();

        var turnRes = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId,
            message = "عايز أعرف المواعيد المتاحة بكره",
            mimeType = "audio/wav"
        });

        Assert.Equal(HttpStatusCode.OK, turnRes.StatusCode);
        var turnDoc = await turnRes.Content.ReadFromJsonAsync<JsonElement>();
        var replyText = turnDoc.GetProperty("text").GetString();

        Assert.NotNull(replyText);
        // Must preserve the alternative dates statement and suggest from these alternatives
        Assert.Contains("ولكن توجد أقرب مواعيد أخرى متاحة", replyText);
        Assert.Contains("تحب نقترح على حضرتك ميعاد من هذه البدائل؟", replyText);
    }

    [Fact]
    public async Task VoiceTurn_WhenLlmFinalTextEmpty_AndSystemHasNoSlots_ReturnsNoSlotsSuggestion()
    {
        var dbName = $"fallback_empty_test_{Guid.NewGuid():N}.db";
        using var factory = CreateTestFactory(dbName);
        var client = factory.CreateClient();

        Guid convId;
        Guid agentId;
        string customerToken;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();

            var agent = new Agent
            {
                Id = Guid.NewGuid(),
                Name = "سارة",
                SystemPrompt = "عيادة النور",
                AllowedToolsJson = "[\"CheckAvailability\"]"
            };
            db.Agents.Add(agent);

            var conv = new Conversation
            {
                Id = Guid.NewGuid(),
                AgentId = agent.Id,
                CustomerPhoneNumber = "01077776666",
                Channel = "Voice",
                StartedAtUtc = DateTime.UtcNow
            };
            db.Conversations.Add(conv);
            // System has ZERO availability slots
            await db.SaveChangesAsync();

            convId = conv.Id;
            agentId = agent.Id;

            var sec = scope.ServiceProvider.GetRequiredService<ISecurityService>();
            customerToken = sec.GenerateCustomerToken(conv.Id, null);
        }

        client.DefaultRequestHeaders.Add("X-Customer-Token", customerToken);
        var sessRes = await client.PostAsJsonAsync("/api/voice/session", new { conversationId = convId });
        var sessDoc = await sessRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = sessDoc.GetProperty("sessionId").GetString();

        var turnRes = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId,
            message = "عايز أعرف المواعيد المتاحة بكره",
            mimeType = "audio/wav"
        });

        Assert.Equal(HttpStatusCode.OK, turnRes.StatusCode);
        var turnDoc = await turnRes.Content.ReadFromJsonAsync<JsonElement>();
        var replyText = turnDoc.GetProperty("text").GetString();

        Assert.NotNull(replyText);
        Assert.Contains("لا توجد مواعيد متاحة حالياً في التاريخ المطلوب بعيادة النور التخصصية، تحب نقترح على حضرتك أقرب موعد بديل؟", replyText);
    }

    [Fact]
    public async Task VoiceTurn_WhenLlmFinalTextEmpty_AndOnlyStalePreviousTurnExists_DoesNotUseStaleAvailability()
    {
        var dbName = $"fallback_stale_test_{Guid.NewGuid():N}.db";
        using var factory = CreateTestFactory(dbName);
        var client = factory.CreateClient();

        Guid convId;
        Guid agentId;
        string customerToken;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();

            var agent = new Agent
            {
                Id = Guid.NewGuid(),
                Name = "سارة",
                SystemPrompt = "عيادة النور",
                AllowedToolsJson = "[\"CheckAvailability\"]"
            };
            db.Agents.Add(agent);

            var conv = new Conversation
            {
                Id = Guid.NewGuid(),
                AgentId = agent.Id,
                CustomerPhoneNumber = "01055554444",
                Channel = "Voice",
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-20)
            };
            db.Conversations.Add(conv);

            // Add a STALE CheckAvailability execution from 15 minutes ago
            var staleResult = new ToolResult(
                Success: true,
                Message: "تم العثور على 5 مواعيد متاحة في 2026-10-09.",
                Data: new { availableSlots = new[] { new { slotId = Guid.NewGuid(), service = "كشف باطنة عامة" } } }
            );
            db.ToolExecutions.Add(new ToolExecution
            {
                Id = Guid.NewGuid(),
                ConversationId = conv.Id,
                ToolName = "CheckAvailability",
                ArgumentsJson = "{}",
                ResultJson = JsonSerializer.Serialize(staleResult),
                Status = "Success",
                DurationMs = 15,
                ExecutedAtUtc = DateTime.UtcNow.AddMinutes(-15) // Stale!
            });
            await db.SaveChangesAsync();

            convId = conv.Id;
            agentId = agent.Id;

            var sec = scope.ServiceProvider.GetRequiredService<ISecurityService>();
            customerToken = sec.GenerateCustomerToken(conv.Id, null);
        }

        client.DefaultRequestHeaders.Add("X-Customer-Token", customerToken);
        var sessRes = await client.PostAsJsonAsync("/api/voice/session", new { conversationId = convId });
        var sessDoc = await sessRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = sessDoc.GetProperty("sessionId").GetString();

        // In the current turn, user does NOT ask for availability (e.g. says "شكراً يا فندم")
        // Therefore, CheckAvailability is NOT executed in this turn.
        var turnRes = await client.PostAsJsonAsync("/api/voice/turn", new
        {
            sessionId,
            conversationId = convId,
            agentId,
            message = "شكراً يا فندم",
            mimeType = "audio/wav"
        });

        Assert.Equal(HttpStatusCode.OK, turnRes.StatusCode);
        var turnDoc = await turnRes.Content.ReadFromJsonAsync<JsonElement>();
        var replyText = turnDoc.GetProperty("text").GetString();

        Assert.NotNull(replyText);
        // Stale availability tool execution MUST NOT be used for this turn
        Assert.DoesNotContain("تم العثور على", replyText);
        Assert.DoesNotContain("تحب أحجز لحضرتك ميعاد في المواعيد المتاحة؟", replyText);
        Assert.Contains("أهلاً بحضرتك يا فندم في عيادة النور التخصصية، ممكن توضح طلبك", replyText);
    }
}
