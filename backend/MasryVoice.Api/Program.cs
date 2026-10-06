using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MasryVoice.Api.Infrastructure.Persistence;
using MasryVoice.Api.Infrastructure.Providers;
using MasryVoice.Api.Features.Tools;
using MasryVoice.Api.Features.Bookings;
using MasryVoice.Api.Features.Chat;
using MasryVoice.Api.Features.Security;
using MasryVoice.Api.Features.Knowledge;
using MasryVoice.Api.Features.Voice;
using MasryVoice.Api.Features.Automation;
using MasryVoice.Api.Features.Telephony;
using MasryVoice.Api.Common;
using MasryVoice.Api.Domain;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// 2. Configure In-Memory Cache
builder.Services.AddMemoryCache();

// 3. Configure Concurrency & Rate Limiting (Separates normal API traffic from expensive LLM inference)
builder.Services.AddSingleton<ConversationLockManager>();
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var opts = new InferenceThrottleOptions
    {
        MaxConcurrentInference = cfg.GetValue<int>("Inference:MaxConcurrency", 1),
        MaxQueueLength = cfg.GetValue<int>("Inference:MaxQueueLength", 5),
        QueueWaitTimeoutSeconds = cfg.GetValue<int>("Inference:QueueWaitTimeoutSeconds", 15)
    };
    return new InferenceThrottlingManager(opts);
});

// ASP.NET Core Bounded RateLimiter for general API traffic and expensive inference
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        string retryAfterSeconds = "10";
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            retryAfterSeconds = Math.Max(1, (int)retryAfter.TotalSeconds).ToString();
        }
        context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds;
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "TOO_MANY_REQUESTS",
            message = "تم تجاوز حد الطلبات المسموح به. يرجى الانتظار والمحاولة لاحقاً.",
            retryAfterSeconds = int.Parse(retryAfterSeconds)
        }), token);
    };

    // Global / API policy: 60 req/min per IP
    options.AddPolicy("api", httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";
        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    // Dedicated policy for expensive LLM / Speech inference endpoints
    options.AddPolicy("inference", httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";
        return RateLimitPartition.GetTokenBucketLimiter(clientIp, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 20,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            QueueLimit = 0
        });
    });

    // Test-controllable policy for rate limit verification
    options.AddPolicy("test-rate-limit", httpContext =>
    {
        var partitionKey = httpContext.Request.Headers["X-Test-Client-Id"].FirstOrDefault()
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "default-client";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromSeconds(5),
            QueueLimit = 0
        });
    });
});

// 4. Configure Database (PostgreSQL or SQLite fallback)
var dbProvider = builder.Configuration["DatabaseProvider"] ?? "Sqlite";
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (dbProvider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
    {
        var cs = builder.Configuration.GetConnectionString("PostgreSql");
        options.UseNpgsql(cs, o => o.UseVector());
    }
    else
    {
        var cs = builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=masryvoice.db";
        options.UseSqlite(cs);
    }
});

// 5. Register HTTP Client & LLM Provider
builder.Services.AddHttpClient();

var llmChoice = builder.Configuration["LlmProvider"] ?? "Ollama";
if (llmChoice.Equals("DeterministicFake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ILlmProvider, DeterministicFakeLlmProvider>();
}
else
{
    builder.Services.AddHttpClient<OllamaLlmProvider>();
    builder.Services.AddTransient<ILlmProvider, OllamaLlmProvider>();
}

// 6. Register Security & Auth Service
builder.Services.AddSingleton<ISecurityService, SecurityService>();

// 7. Register Knowledge & Embedding Services
var embProviderType = builder.Configuration["Inference:EmbeddingProvider"] ?? "Deterministic";
if (embProviderType.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
    embProviderType.Equals("Real", StringComparison.OrdinalIgnoreCase))
{
    var ollamaUrl = builder.Configuration["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
    builder.Services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>(c =>
    {
        c.BaseAddress = new Uri(ollamaUrl);
        c.Timeout = TimeSpan.FromSeconds(30);
    });
}
else
{
    builder.Services.AddSingleton<IEmbeddingProvider, DeterministicEmbeddingProvider>();
}
builder.Services.AddScoped<IKnowledgeService, KnowledgeService>();

// 8. Register Booking Confirmation Application Service (Server-Side Customer Action)
builder.Services.AddScoped<IBookingConfirmationService, BookingConfirmationService>();

// 9. Register LLM Tools & Registry (Excludes confirmation to preserve trust boundary)
builder.Services.AddScoped<CheckAvailabilityTool>();
builder.Services.AddScoped<StageBookingTool>();
builder.Services.AddScoped<GetBookingTool>();
builder.Services.AddScoped<SearchKnowledgeBaseTool>();
builder.Services.AddScoped<ITool>(sp => sp.GetRequiredService<CheckAvailabilityTool>());
builder.Services.AddScoped<ITool>(sp => sp.GetRequiredService<StageBookingTool>());
builder.Services.AddScoped<ITool>(sp => sp.GetRequiredService<GetBookingTool>());
builder.Services.AddScoped<ITool>(sp => sp.GetRequiredService<SearchKnowledgeBaseTool>());
builder.Services.AddScoped<ToolRegistry>();

// 10. Register Chat Orchestrator
builder.Services.AddScoped<AgentOrchestrator>();

// 11. Register Speech Pipeline & Voice Session
var sttProviderType = builder.Configuration["Voice:SttProvider"] ?? "Simulated";
if (sttProviderType.Equals("Whisper", StringComparison.OrdinalIgnoreCase) ||
    sttProviderType.Equals("Real", StringComparison.OrdinalIgnoreCase))
{
    var sttBaseUrl = builder.Configuration["Voice:SttBaseUrl"] ?? "http://127.0.0.1:8000";
    builder.Services.AddHttpClient<ISttProvider, WhisperSttProvider>(c =>
    {
        c.BaseAddress = new Uri(sttBaseUrl);
        c.Timeout = TimeSpan.FromSeconds(30);
    });
}
else
{
    builder.Services.AddSingleton<ISttProvider, SimulatedSttProvider>();
}

var ttsProviderType = builder.Configuration["Voice:TtsProvider"] ?? "Simulated";
if (ttsProviderType.Equals("LocalEgyptian", StringComparison.OrdinalIgnoreCase) ||
    ttsProviderType.Equals("Real", StringComparison.OrdinalIgnoreCase))
{
    var ttsBaseUrl = builder.Configuration["Voice:TtsBaseUrl"] ?? "http://127.0.0.1:8000";
    builder.Services.AddHttpClient<ITtsProvider, LocalEgyptianTtsProvider>(c =>
    {
        c.BaseAddress = new Uri(ttsBaseUrl);
        c.Timeout = TimeSpan.FromSeconds(30);
    });
}
else
{
    builder.Services.AddSingleton<ITtsProvider, SimulatedTtsProvider>();
}
builder.Services.AddSingleton<VoiceSessionManager>();

// 12. Register Durable Outbox Processor Background Service
builder.Services.AddHostedService<DurableOutboxProcessor>();

// 13. Register Asterisk AudioSocket Telephony Adapter
builder.Services.AddSingleton<AsteriskAudioSocketService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AsteriskAudioSocketService>());
builder.Services.AddSingleton<ITelephonyAdapter>(sp => sp.GetRequiredService<AsteriskAudioSocketService>());

var app = builder.Build();

app.UseCors();
app.UseRateLimiter();

// Schema initialization & optional development/test seeding
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    bool shouldInitSchema = cfg.GetValue<bool>("Database:InitializeSchema", true);
    bool shouldSeed = cfg.GetValue<bool>("Database:AutoSeed", env.IsDevelopment() || env.EnvironmentName == "Testing");

    if (shouldInitSchema)
    {
        await db.Database.EnsureCreatedAsync();
    }

    if (shouldSeed)
    {
        await db.SeedInitialDataAsync();
    }
}

// ----------------------------------------------------
// API Endpoints
// ----------------------------------------------------

// Health & System Status Endpoint (With Inference, Automation & Telephony Metrics)
app.MapGet("/api/health", async (
    AppDbContext db,
    IConfiguration cfg,
    InferenceThrottlingManager throttling,
    ITelephonyAdapter telephony,
    VoiceSessionManager voiceSessions) =>
{
    var nowCairo = CairoTimeHelper.NowCairo;
    var isBusinessHours = CairoTimeHelper.IsBusinessHours(nowCairo);

    bool dbOk = false;
    int pendingOutbox = 0;
    int deadLetterOutbox = 0;
    try
    {
        dbOk = await db.Database.CanConnectAsync();
        if (dbOk)
        {
            pendingOutbox = await db.OutboxJobs.CountAsync(j => j.Status == "Pending");
            deadLetterOutbox = await db.OutboxJobs.CountAsync(j => j.Status == "DeadLetter");
        }
    }
    catch { }

    return Results.Ok(new
    {
        status = "Healthy",
        timestampUtc = DateTime.UtcNow,
        cairoTime = nowCairo.ToString("yyyy-MM-dd HH:mm:ss"),
        isCairoBusinessHours = isBusinessHours,
        businessSchedule = "Sun-Thu 09:00 - 17:00 (Africa/Cairo)",
        databaseConnected = dbOk,
        configuredLlmProvider = cfg["LlmProvider"] ?? "Ollama",
        defaultModel = cfg["Ollama:DefaultModel"] ?? "qwen2.5:3b",
        inferenceLoad = new
        {
            active = throttling.ActiveCount,
            waitingInQueue = throttling.WaitingCount,
            totalRejections = throttling.TotalRejections,
            totalProcessed = throttling.TotalProcessed
        },
        automation = new
        {
            pendingJobs = pendingOutbox,
            deadLetterJobs = deadLetterOutbox
        },
        telephony = new
        {
            isListening = telephony.IsListening,
            activeCalls = telephony.ActiveCallsCount
        }
    });
});

// Agent Management Endpoints (With AsNoTracking and Cache Invalidation)
app.MapGet("/api/agents", async (AppDbContext db) =>
{
    var agents = await db.Agents.AsNoTracking().OrderBy(a => a.CreatedAtUtc).ToListAsync();
    return Results.Ok(agents.Select(a => new
    {
        id = a.Id,
        name = a.Name,
        systemPrompt = a.SystemPrompt,
        modelName = a.ModelName,
        languageCode = a.LanguageCode,
        temperature = a.Temperature,
        isActive = a.IsActive,
        allowedTools = a.GetAllowedTools(),
        createdAtUtc = a.CreatedAtUtc
    }));
});

app.MapPost("/api/agents", async (AppDbContext db, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, AgentUpdateRequest req) =>
{
    var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == req.Id);
    if (agent == null)
    {
        agent = new Agent { Id = req.Id == Guid.Empty ? Guid.NewGuid() : req.Id };
        db.Agents.Add(agent);
    }

    agent.Name = req.Name;
    agent.SystemPrompt = req.SystemPrompt;
    agent.ModelName = req.ModelName;
    agent.LanguageCode = req.LanguageCode;
    agent.Temperature = req.Temperature;
    agent.IsActive = req.IsActive;
    agent.SetAllowedTools(req.AllowedTools);

    await db.SaveChangesAsync();

    // Invalidate cached agent definition
    cache.Remove($"agent_{agent.Id}");

    return Results.Ok(agent);
});

// Available Slots Endpoint (With AsNoTracking & 15s Read-Cache with Invalidation)
app.MapGet("/api/slots", async (AppDbContext db, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, string? date) =>
{
    var cacheKey = $"slots_{date ?? "all"}";
    if (cache.TryGetValue(cacheKey, out object? cachedSlots) && cachedSlots != null)
    {
        return Results.Ok(cachedSlots);
    }

    var query = db.AvailabilitySlots.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var parsedDate))
    {
        var startUtc = CairoTimeHelper.CairoToUtc(parsedDate.Date);
        var endUtc = CairoTimeHelper.CairoToUtc(parsedDate.Date.AddDays(1));
        query = query.Where(s => s.StartTimeUtc >= startUtc && s.StartTimeUtc < endUtc);
    }
    else
    {
        query = query.Where(s => s.StartTimeUtc >= DateTime.UtcNow);
    }

    var slots = await query.OrderBy(s => s.StartTimeUtc).Take(20).ToListAsync();
    var resultList = slots.Select(s => new
    {
        id = s.Id,
        serviceName = s.ServiceName,
        startTimeUtc = s.StartTimeUtc,
        endTimeUtc = s.EndTimeUtc,
        cairoTimeFormatted = CairoTimeHelper.FormatCairoFriendly(s.StartTimeUtc),
        totalCapacity = s.TotalCapacity,
        bookedCapacity = s.BookedCapacity,
        isAvailable = s.IsAvailable
    }).ToList();

    cache.Set(cacheKey, resultList, TimeSpan.FromSeconds(15));
    return Results.Ok(resultList);
});

// Bookings List Endpoint (Admin Protected)
app.MapGet("/api/bookings", async (HttpContext ctx, AppDbContext db, ISecurityService security) =>
{
    var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
    if (!security.ValidateAdminKey(authHeader))
    {
        return Results.Unauthorized();
    }

    var bookings = await db.Bookings
        .AsNoTracking()
        .Include(b => b.Slot)
        .OrderByDescending(b => b.CreatedAtUtc)
        .Take(50)
        .ToListAsync();

    return Results.Ok(bookings.Select(b => new
    {
        id = b.Id,
        slotId = b.SlotId,
        customerName = b.CustomerName,
        customerPhone = b.CustomerPhone,
        serviceName = b.ServiceName,
        bookingDateUtc = b.BookingDateUtc,
        cairoTimeFormatted = CairoTimeHelper.FormatCairoFriendly(b.BookingDateUtc),
        status = b.Status,
        idempotencyKey = b.IdempotencyKey,
        createdAtUtc = b.CreatedAtUtc
    }));
});

// Single Booking Lookup Endpoint (Protected: Admin or Authorized Customer)
app.MapGet("/api/bookings/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, ISecurityService security) =>
{
    var booking = await db.Bookings.AsNoTracking().Include(b => b.Slot).FirstOrDefaultAsync(b => b.Id == id);
    if (booking == null) return Results.NotFound();

    var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
    var customerToken = ctx.Request.Headers["X-Customer-Token"].FirstOrDefault();

    bool isAdmin = security.ValidateAdminKey(authHeader);
    bool isCustomer = false;
    if (!isAdmin && !string.IsNullOrEmpty(customerToken))
    {
        isCustomer = security.ValidateCustomerAccess(customerToken, Guid.Empty, booking.CustomerPhone);
    }

    if (!isAdmin && !isCustomer)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Ok(new
    {
        id = booking.Id,
        slotId = booking.SlotId,
        customerName = booking.CustomerName,
        customerPhone = booking.CustomerPhone,
        serviceName = booking.ServiceName,
        bookingDateUtc = booking.BookingDateUtc,
        cairoTimeFormatted = CairoTimeHelper.FormatCairoFriendly(booking.BookingDateUtc),
        status = booking.Status,
        idempotencyKey = booking.IdempotencyKey,
        createdAtUtc = booking.CreatedAtUtc
    });
});

// Pending Bookings Endpoint (AsNoTracking)
app.MapGet("/api/bookings/pending", async (AppDbContext db, Guid? conversationId) =>
{
    var query = db.PendingBookings.AsNoTracking().Include(pb => pb.Slot).AsQueryable();
    if (conversationId.HasValue && conversationId.Value != Guid.Empty)
    {
        query = query.Where(pb => pb.ConversationId == conversationId.Value);
    }

    var pending = await query.OrderByDescending(pb => pb.CreatedAtUtc).Take(20).ToListAsync();
    return Results.Ok(pending.Select(pb => new
    {
        id = pb.Id,
        conversationId = pb.ConversationId,
        customerName = pb.CustomerName,
        customerPhone = pb.CustomerPhone,
        serviceName = pb.ServiceName,
        cairoTimeFormatted = CairoTimeHelper.FormatCairoFriendly(pb.BookingDateUtc),
        requestHash = pb.RequestHash,
        status = pb.Status,
        createdAtUtc = pb.CreatedAtUtc
    }));
});

// Stage Pending Booking Draft Endpoint
app.MapPost("/api/bookings/stage", async (
    AppDbContext db,
    [Microsoft.AspNetCore.Mvc.FromBody] StageBookingRequest req,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.CustomerName) || string.IsNullOrWhiteSpace(req.CustomerPhone))
    {
        return Results.BadRequest(new { success = false, message = "CustomerName and CustomerPhone are required." });
    }

    AvailabilitySlot? slot = null;
    if (req.SlotId.HasValue && req.SlotId.Value != Guid.Empty)
    {
        slot = await db.AvailabilitySlots.FirstOrDefaultAsync(s => s.Id == req.SlotId.Value, ct);
    }
    else
    {
        slot = await db.AvailabilitySlots
            .Where(s => s.BookedCapacity < s.TotalCapacity && s.StartTimeUtc >= DateTime.UtcNow)
            .OrderBy(s => s.StartTimeUtc)
            .FirstOrDefaultAsync(ct);
    }

    if (slot == null)
    {
        return Results.NotFound(new { success = false, message = "Requested slot was not found or is no longer available." });
    }

    if (slot.BookedCapacity >= slot.TotalCapacity)
    {
        return Results.BadRequest(new { success = false, message = "This slot is already fully booked." });
    }

    var serviceName = string.IsNullOrWhiteSpace(req.ServiceName) ? slot.ServiceName : req.ServiceName;
    var requestHash = PendingBooking.ComputeRequestHash(slot.Id, req.CustomerPhone, req.CustomerName, serviceName);

    var convId = req.ConversationId == Guid.Empty ? Guid.NewGuid() : req.ConversationId;
    var conv = await db.Conversations.FirstOrDefaultAsync(c => c.Id == convId, ct);
    if (conv == null)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(ct);
        conv = new Conversation
        {
            Id = convId,
            AgentId = agent?.Id ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CustomerName = req.CustomerName,
            CustomerPhoneNumber = req.CustomerPhone,
            Channel = "web",
            Status = "Active",
            StartedAtUtc = DateTime.UtcNow,
            LastActiveAtUtc = DateTime.UtcNow
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync(ct);
    }

    var idempotencyKey = $"idemp_{convId:N}_{slot.Id:N}_{req.CustomerPhone}";
    var pending = await db.PendingBookings.FirstOrDefaultAsync(pb => pb.ConversationId == convId, ct);
    if (pending != null)
    {
        pending.SlotId = slot.Id;
        pending.CustomerName = req.CustomerName;
        pending.CustomerPhone = req.CustomerPhone;
        pending.ServiceName = serviceName;
        pending.BookingDateUtc = slot.StartTimeUtc;
        pending.RequestHash = requestHash;
        pending.IdempotencyKey = idempotencyKey;
        pending.Status = "Pending";
        pending.CreatedAtUtc = DateTime.UtcNow;
    }
    else
    {
        pending = new PendingBooking
        {
            Id = Guid.NewGuid(),
            ConversationId = convId,
            SlotId = slot.Id,
            CustomerName = req.CustomerName,
            CustomerPhone = req.CustomerPhone,
            ServiceName = serviceName,
            BookingDateUtc = slot.StartTimeUtc,
            RequestHash = requestHash,
            IdempotencyKey = idempotencyKey,
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow
        };
        db.PendingBookings.Add(pending);
    }

    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        success = true,
        pendingBookingId = pending.Id,
        conversationId = pending.ConversationId,
        slotId = slot.Id,
        customerName = pending.CustomerName,
        customerPhone = pending.CustomerPhone,
        serviceName = pending.ServiceName,
        requestHash = pending.RequestHash,
        cairoTime = CairoTimeHelper.FormatCairoFriendly(slot.StartTimeUtc),
        status = pending.Status
    });
});

// Explicit Customer Confirmation Action Endpoint
app.MapPost("/api/bookings/confirm", async (
    IBookingConfirmationService confirmationService,
    ISecurityService security,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
    ConfirmBookingRequest req,
    CancellationToken ct) =>
{
    var result = await confirmationService.ConfirmPendingBookingAsync(
        req.ConversationId,
        req.PendingBookingId,
        req.ExpectedRequestHash,
        ct);

    if (!result.Success)
    {
        return Results.BadRequest(result);
    }

    // Generate signed customer token for future verified access
    string? customerToken = null;
    if (result.Data != null)
    {
        var phone = ((dynamic)result.Data).customerPhone as string;
        customerToken = security.GenerateCustomerToken(req.ConversationId, phone);
    }

    cache.Remove("slots_all");

    return Results.Ok(new
    {
        result.Success,
        result.Message,
        result.Data,
        customerToken
    });
});

// Conversation History & Tool Execution Logs (Protected: Admin or Customer)
app.MapGet("/api/conversations/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, ISecurityService security) =>
{
    var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
    var customerToken = ctx.Request.Headers["X-Customer-Token"].FirstOrDefault();

    bool isAdmin = security.ValidateAdminKey(authHeader);
    bool isCustomer = security.ValidateCustomerAccess(customerToken, id);

    if (!isAdmin && !isCustomer)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var conversation = await db.Conversations
        .AsNoTracking()
        .AsSplitQuery()
        .Include(c => c.Messages.OrderBy(m => m.SequenceNumber))
        .Include(c => c.ToolExecutions.OrderBy(t => t.ExecutedAtUtc))
        .FirstOrDefaultAsync(c => c.Id == id);

    if (conversation == null)
    {
        return Results.NotFound(new { message = "المحادثة غير موجودة." });
    }

    return Results.Ok(new
    {
        id = conversation.Id,
        agentId = conversation.AgentId,
        customerName = conversation.CustomerName,
        customerPhone = conversation.CustomerPhoneNumber,
        channel = conversation.Channel,
        status = conversation.Status,
        startedAtUtc = conversation.StartedAtUtc,
        messages = conversation.Messages.Select(m => new
        {
            id = m.Id,
            role = m.Role,
            content = m.Content,
            toolCallId = m.ToolCallId,
            toolName = m.ToolName,
            sequenceNumber = m.SequenceNumber,
            createdAtUtc = m.CreatedAtUtc
        }),
        toolExecutions = conversation.ToolExecutions.Select(t => new
        {
            id = t.Id,
            toolName = t.ToolName,
            arguments = t.ArgumentsJson,
            result = t.ResultJson,
            status = t.Status,
            durationMs = t.DurationMs,
            executedAtUtc = t.ExecutedAtUtc
        })
    });
});

// Server-Sent Events (SSE) Streaming Text Chat Endpoint
app.MapPost("/api/chat/stream", async (
    HttpContext httpContext,
    AgentOrchestrator orchestrator,
    ISecurityService security,
    [Microsoft.AspNetCore.Mvc.FromBody] ChatStreamRequest request,
    CancellationToken ct) =>
{
    httpContext.Response.Headers.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    var convId = request.ConversationId == Guid.Empty ? Guid.NewGuid() : request.ConversationId;
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, httpContext.RequestAborted);

    // Issue customer session token
    var customerToken = security.GenerateCustomerToken(convId, null);
    var initEvent = JsonSerializer.Serialize(new
    {
        conversationId = convId,
        type = "session",
        customerToken
    });
    await httpContext.Response.WriteAsync($"data: {initEvent}\n\n", linkedCts.Token);
    await httpContext.Response.Body.FlushAsync(linkedCts.Token);

    try
    {
        await foreach (var chatEvent in orchestrator.ProcessUserMessageAsync(request.AgentId, convId, request.Message, linkedCts.Token))
        {
            var jsonEvent = JsonSerializer.Serialize(new
            {
                conversationId = convId,
                type = chatEvent.EventType,
                content = chatEvent.Content,
                metadata = chatEvent.Metadata
            });

            await httpContext.Response.WriteAsync($"data: {jsonEvent}\n\n", linkedCts.Token);
            await httpContext.Response.Body.FlushAsync(linkedCts.Token);
        }
    }
    catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
    {
        // Client disconnected; cancellation propagated to release locks & permits
    }
}).RequireRateLimiting("inference");

// ----------------------------------------------------
// Knowledge Management & RAG Endpoints
// ----------------------------------------------------

app.MapPost("/api/knowledge/ingest", async (HttpContext ctx, IKnowledgeService knowledge, ISecurityService security, [Microsoft.AspNetCore.Mvc.FromBody] IngestDocumentRequest req) =>
{
    var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
    if (!security.ValidateAdminKey(authHeader)) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
    {
        return Results.BadRequest(new { message = "Title and Content are required." });
    }

    var doc = await knowledge.IngestDocumentAsync(req.Title, req.FileName ?? "doc.md", req.Content, req.Category ?? "General");
    return Results.Ok(new { doc.Id, doc.Title, doc.ChunkCount, doc.CreatedAtUtc });
});

app.MapGet("/api/knowledge/documents", async (HttpContext ctx, AppDbContext db, ISecurityService security) =>
{
    var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
    if (!security.ValidateAdminKey(authHeader)) return Results.Unauthorized();

    var docs = await db.KnowledgeDocuments.AsNoTracking().OrderByDescending(d => d.CreatedAtUtc).ToListAsync();
    return Results.Ok(docs);
});

app.MapGet("/api/knowledge/search", async (IKnowledgeService knowledge, string query) =>
{
    if (string.IsNullOrWhiteSpace(query)) return Results.BadRequest(new { message = "Query parameter is required." });
    var results = await knowledge.SearchAsync(query);
    return Results.Ok(results);
});

// ----------------------------------------------------
// Voice Pipeline Endpoints
// ----------------------------------------------------

app.MapPost("/api/voice/session", (VoiceSessionManager sessionMgr, [Microsoft.AspNetCore.Mvc.FromBody] VoiceSessionRequest req) =>
{
    var convId = req.ConversationId == Guid.Empty ? Guid.NewGuid() : req.ConversationId;
    var sessionId = Guid.NewGuid();
    var session = sessionMgr.GetOrCreateSession(sessionId, convId);
    return Results.Ok(new { sessionId, conversationId = convId });
});

app.MapPost("/api/voice/interrupt", (VoiceSessionManager sessionMgr, [Microsoft.AspNetCore.Mvc.FromBody] VoiceInterruptRequest req) =>
{
    var interrupted = sessionMgr.Interrupt(req.SessionId);
    return Results.Ok(new { sessionId = req.SessionId, interrupted });
});

app.MapPost("/api/voice/stt", async (ISttProvider stt, HttpRequest request, CancellationToken ct) =>
{
    if (!request.HasFormContentType || request.Form.Files.Count == 0)
    {
        var text = await stt.TranscribeAudioAsync(request.Body, request.ContentType ?? "audio/wav", "ar", ct);
        return Results.Ok(new { text });
    }

    var file = request.Form.Files[0];
    await using var stream = file.OpenReadStream();
    var transcribed = await stt.TranscribeAudioAsync(stream, file.ContentType, "ar", ct);
    return Results.Ok(new { text = transcribed });
}).RequireRateLimiting("inference");

app.MapPost("/api/voice/tts", async (ITtsProvider tts, [Microsoft.AspNetCore.Mvc.FromBody] TtsSynthesizeRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest();
    var audio = await tts.SynthesizeSpeechAsync(req.Text, req.LanguageCode ?? "ar-EG", ct);
    return Results.File(audio.ToArray(), "audio/wav");
}).RequireRateLimiting("inference");

app.MapPost("/api/voice/turn", async (
    VoiceSessionManager sessionMgr,
    AgentOrchestrator orchestrator,
    ITtsProvider tts,
    [Microsoft.AspNetCore.Mvc.FromBody] VoiceTurnRequest req,
    CancellationToken ct) =>
{
    var session = sessionMgr.GetOrCreateSession(req.SessionId, req.ConversationId);
    using var turnContext = session.StartNewTurn();

    var assistantText = new System.Text.StringBuilder();
    try
    {
        await foreach (var chatEvent in orchestrator.ProcessUserMessageAsync(req.AgentId, req.ConversationId, req.Message, turnContext.Token))
        {
            if (turnContext.Token.IsCancellationRequested) break;
            if (chatEvent.EventType == "token" && !string.IsNullOrEmpty(chatEvent.Content))
            {
                assistantText.Append(chatEvent.Content);
            }
        }

        if (turnContext.Token.IsCancellationRequested || !session.IsTurnActive(turnContext.TurnId))
        {
            return Results.StatusCode(499); // Client Closed Request / Interrupted
        }

        var reply = assistantText.ToString();
        var audioBytes = await tts.SynthesizeSpeechAsync(reply, "ar-EG", turnContext.Token);

        return Results.Ok(new
        {
            turnId = turnContext.TurnId,
            sessionId = req.SessionId,
            conversationId = req.ConversationId,
            text = reply,
            audioBase64 = Convert.ToBase64String(audioBytes.ToArray())
        });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499); // Graceful barge-in interruption status
    }
}).RequireRateLimiting("inference");

// Test endpoint for validating rate limiting behavior and headers
app.MapGet("/api/test/rate-limited", () => Results.Ok(new { status = "ok" }))
    .RequireRateLimiting("test-rate-limit");

app.Run();

// ----------------------------------------------------
// DTOs
// ----------------------------------------------------
public record ChatStreamRequest(
    Guid AgentId,
    Guid ConversationId,
    string Message
);

public record AgentUpdateRequest(
    Guid Id,
    string Name,
    string SystemPrompt,
    string ModelName,
    string LanguageCode,
    double Temperature,
    bool IsActive,
    List<string> AllowedTools
);

public record StageBookingRequest(
    Guid ConversationId,
    string CustomerName,
    string CustomerPhone,
    Guid? SlotId = null,
    string? ServiceName = null
);

public record ConfirmBookingRequest(
    Guid ConversationId,
    Guid PendingBookingId,
    string? ExpectedRequestHash = null
);

public record IngestDocumentRequest(
    string Title,
    string? FileName,
    string Content,
    string? Category
);

public record VoiceSessionRequest(
    Guid ConversationId
);

public record VoiceInterruptRequest(
    Guid SessionId
);

public record TtsSynthesizeRequest(
    string Text,
    string? LanguageCode = "ar-EG"
);

public record VoiceTurnRequest(
    Guid SessionId,
    Guid ConversationId,
    Guid AgentId,
    string Message
);

public partial class Program { }
