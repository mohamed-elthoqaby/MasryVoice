using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MasryVoice.Tests;

/// <summary>
/// HTTP Integration tests validating ASP.NET Core rate limiting behavior,
/// 429 Too Many Requests status, Retry-After response headers, client partition isolation, and recovery.
/// </summary>
public class RateLimitingIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RateLimitingIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var dbName = $"ratelimit_test_{Guid.NewGuid():N}.db";
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
                    ["Database:AutoSeed"] = "false",
                    ["LlmProvider"] = "DeterministicFake",
                    ["Voice:SttProvider"] = "Simulated",
                    ["Voice:TtsProvider"] = "Simulated",
                    ["Inference:EmbeddingProvider"] = "Deterministic"
                });
            });
            builder.ConfigureServices(services =>
            {
                var descriptors = services.Where(d => d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<MasryVoice.Api.Infrastructure.Persistence.AppDbContext>) || d.ServiceType == typeof(MasryVoice.Api.Infrastructure.Persistence.AppDbContext)).ToList();
                foreach (var d in descriptors) services.Remove(d);

                services.AddDbContext<MasryVoice.Api.Infrastructure.Persistence.AppDbContext>(options =>
                {
                    options.UseSqlite($"Data Source={dbName}");
                });
            });
        });
    }

    [Fact]
    public async Task RateLimiting_Returns429AndRetryAfter_OnExcessRequests()
    {
        var client = _factory.CreateClient();
        var clientId = $"client-{Guid.NewGuid():N}";
        client.DefaultRequestHeaders.Add("X-Test-Client-Id", clientId);

        // Policy allows 3 requests per 5-second window
        for (int i = 0; i < 3; i++)
        {
            var response = await client.GetAsync("/api/test/rate-limited");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 4th request must be rejected with 429 Too Many Requests
        var rejectedResponse = await client.GetAsync("/api/test/rate-limited");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);

        // Verify Retry-After header is present
        Assert.True(
            rejectedResponse.Headers.Contains("Retry-After") ||
            rejectedResponse.Headers.TryGetValues("Retry-After", out _),
            "Expected Retry-After header in 429 response.");

        var content = await rejectedResponse.Content.ReadAsStringAsync();
        Assert.Contains("TOO_MANY_REQUESTS", content);
    }

    [Fact]
    public async Task RateLimiting_PartitionsClientsIndependently()
    {
        var clientA = _factory.CreateClient();
        var clientB = _factory.CreateClient();

        var clientAId = $"client-A-{Guid.NewGuid():N}";
        var clientBId = $"client-B-{Guid.NewGuid():N}";

        clientA.DefaultRequestHeaders.Add("X-Test-Client-Id", clientAId);
        clientB.DefaultRequestHeaders.Add("X-Test-Client-Id", clientBId);

        // Exhaust client A's quota
        for (int i = 0; i < 3; i++)
        {
            var res = await clientA.GetAsync("/api/test/rate-limited");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        var clientARejected = await clientA.GetAsync("/api/test/rate-limited");
        Assert.Equal(HttpStatusCode.TooManyRequests, clientARejected.StatusCode);

        // Client B must NOT be throttled by Client A's activity
        var clientBResponse = await clientB.GetAsync("/api/test/rate-limited");
        Assert.Equal(HttpStatusCode.OK, clientBResponse.StatusCode);
    }

    [Fact]
    public async Task RateLimiting_RecoversAfterWindowExpires()
    {
        var client = _factory.CreateClient();
        var clientId = $"client-recovery-{Guid.NewGuid():N}";
        client.DefaultRequestHeaders.Add("X-Test-Client-Id", clientId);

        // Exhaust quota
        for (int i = 0; i < 3; i++)
        {
            await client.GetAsync("/api/test/rate-limited");
        }

        var rejected = await client.GetAsync("/api/test/rate-limited");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // Wait for the 5-second fixed window to expire
        await Task.Delay(TimeSpan.FromSeconds(5.5));

        // Client must be allowed again
        var recovered = await client.GetAsync("/api/test/rate-limited");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task RateLimiting_RealApiRoute_EnforcesPolicyAndRejectsWith429()
    {
        var dbName = $"ratelimit_api_{Guid.NewGuid():N}.db";
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:ApiPermitLimit"] = "2",
                    ["DatabaseProvider"] = "Sqlite",
                    ["ConnectionStrings:Sqlite"] = $"Data Source={dbName}"
                });
            });
        });

        var clientA = customFactory.CreateClient();
        var clientB = customFactory.CreateClient();
        var idA = $"client-api-A-{Guid.NewGuid():N}";
        var idB = $"client-api-B-{Guid.NewGuid():N}";
        clientA.DefaultRequestHeaders.Add("X-Test-Client-Id", idA);
        clientB.DefaultRequestHeaders.Add("X-Test-Client-Id", idB);

        // 2 requests allowed under "api" policy on real endpoint /api/slots
        var res1 = await clientA.GetAsync("/api/slots");
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        var res2 = await clientA.GetAsync("/api/slots");
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        // 3rd request rejected with 429 and Retry-After
        var res3 = await clientA.GetAsync("/api/slots");
        Assert.Equal(HttpStatusCode.TooManyRequests, res3.StatusCode);
        Assert.True(res3.Headers.Contains("Retry-After") || res3.Headers.TryGetValues("Retry-After", out _));

        // Isolated Client B can still make requests successfully
        var resB = await clientB.GetAsync("/api/slots");
        Assert.Equal(HttpStatusCode.OK, resB.StatusCode);
    }

    [Fact]
    public async Task RateLimiting_RealInferenceRoute_EnforcesPolicyAndRejectsWith429()
    {
        var dbName = $"ratelimit_inf_{Guid.NewGuid():N}.db";
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:InferenceTokenLimit"] = "2",
                    ["RateLimiting:InferenceTokensPerPeriod"] = "1",
                    ["DatabaseProvider"] = "Sqlite",
                    ["ConnectionStrings:Sqlite"] = $"Data Source={dbName}"
                });
            });
        });

        var clientA = customFactory.CreateClient();
        var clientB = customFactory.CreateClient();
        var idA = $"client-inf-A-{Guid.NewGuid():N}";
        var idB = $"client-inf-B-{Guid.NewGuid():N}";
        clientA.DefaultRequestHeaders.Add("X-Test-Client-Id", idA);
        clientB.DefaultRequestHeaders.Add("X-Test-Client-Id", idB);

        // 2 requests allowed under "inference" policy on /api/knowledge/search
        var res1 = await clientA.GetAsync("/api/knowledge/search?query=test");
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        var res2 = await clientA.GetAsync("/api/knowledge/search?query=test");
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        // 3rd request rejected with 429 and Retry-After
        var res3 = await clientA.GetAsync("/api/knowledge/search?query=test");
        Assert.Equal(HttpStatusCode.TooManyRequests, res3.StatusCode);
        Assert.True(res3.Headers.Contains("Retry-After") || res3.Headers.TryGetValues("Retry-After", out _));

        // Isolated Client B can still query knowledge base
        var resB = await clientB.GetAsync("/api/knowledge/search?query=test");
        Assert.Equal(HttpStatusCode.OK, resB.StatusCode);
    }
}
