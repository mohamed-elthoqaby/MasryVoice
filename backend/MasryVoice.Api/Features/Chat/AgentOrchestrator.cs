using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MasryVoice.Api.Infrastructure.Persistence;
using MasryVoice.Api.Infrastructure.Providers;
using MasryVoice.Api.Features.Tools;
using MasryVoice.Api.Domain;

namespace MasryVoice.Api.Features.Chat;

public record ChatEvent(
    string EventType, // "token", "tool_call", "tool_result", "error", "done"
    string? Content = null,
    object? Metadata = null
);

/// <summary>Stable machine-readable metadata attached to every "error" ChatEvent.</summary>
public record ChatErrorInfo(
    [property: System.Text.Json.Serialization.JsonPropertyName("code")] string Code,
    [property: System.Text.Json.Serialization.JsonPropertyName("stage")] string Stage,
    [property: System.Text.Json.Serialization.JsonPropertyName("retryAfterSeconds")] int? RetryAfterSeconds = null);

public class AgentOrchestrator
{
    private readonly AppDbContext _db;
    private readonly ILlmProvider _llmProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly IMemoryCache _cache;
    private readonly ConversationLockManager _lockManager;
    private readonly InferenceThrottlingManager _throttlingManager;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        AppDbContext db,
        ILlmProvider llmProvider,
        ToolRegistry toolRegistry,
        IMemoryCache cache,
        ConversationLockManager lockManager,
        InferenceThrottlingManager throttlingManager,
        ILogger<AgentOrchestrator> logger)
    {
        _db = db;
        _llmProvider = llmProvider;
        _toolRegistry = toolRegistry;
        _cache = cache;
        _lockManager = lockManager;
        _throttlingManager = throttlingManager;
        _logger = logger;
    }

    public async IAsyncEnumerable<ChatEvent> ProcessUserMessageAsync(
        Guid agentId,
        Guid conversationId,
        string userMessageText,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 0. Acquire per-conversation lock to guarantee message ordering and avoid concurrent state corruption
        using var convLock = await _lockManager.AcquireLockAsync(conversationId, ct);

        // 1. Fetch Agent with memory caching to eliminate per-turn DB lookups
        var cacheKey = $"agent_{agentId}";
        if (!_cache.TryGetValue(cacheKey, out Agent? agent) || agent == null)
        {
            agent = await _db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.IsActive, ct);
            if (agent != null)
            {
                _cache.Set(cacheKey, agent, TimeSpan.FromMinutes(10));
            }
        }

        if (agent == null)
        {
            yield return new ChatEvent("error", "الوكيل المطلوب غير موجود أو غير مفعل.", new ChatErrorInfo("AGENT_NOT_FOUND", "agent"));
            yield break;
        }

        var allowedToolNames = agent.GetAllowedTools();
        var allowedToolDefinitions = _toolRegistry.GetAllowedDefinitions(allowedToolNames).ToList();

        // 2. Fetch or initialize conversation (avoid loading all messages)
        var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation == null)
        {
            conversation = new Conversation
            {
                Id = conversationId,
                AgentId = agentId,
                Status = "Active",
                StartedAtUtc = DateTime.UtcNow,
                LastActiveAtUtc = DateTime.UtcNow
            };
            _db.Conversations.Add(conversation);
            await _db.SaveChangesAsync(ct);
        }

        // 3. Save incoming user message with indexed sequence calculation
        var maxSeq = await _db.Messages
            .Where(m => m.ConversationId == conversation.Id)
            .MaxAsync(m => (int?)m.SequenceNumber, ct) ?? 0;
        var nextSeq = maxSeq + 1;

        var userMsgEntity = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = userMessageText,
            SequenceNumber = nextSeq,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.Messages.Add(userMsgEntity);
        conversation.LastActiveAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // 4. Build LLM Conversation Context (bounded to last 20 messages for predictable prompt eval latency)
        var llmMessages = new List<LlmChatMessage>
        {
            new("system", agent.SystemPrompt)
        };

        var history = await _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.SequenceNumber)
            .Take(20)
            .OrderBy(m => m.SequenceNumber)
            .ToListAsync(ct);

        foreach (var msg in history)
        {
            if (msg.Role == "tool") continue;
            if (msg.Role == "assistant" && string.IsNullOrWhiteSpace(msg.Content)) continue;

            llmMessages.Add(new LlmChatMessage(
                Role: msg.Role,
                Content: msg.Content
            ));
        }

        // 5. Bounded Tool-Calling Loop (max 5 iterations)
        int iterations = 0;
        const int maxIterations = 5;

        while (iterations < maxIterations && !ct.IsCancellationRequested)
        {
            iterations++;
            var request = new LlmChatRequest(
                Model: agent.ModelName,
                Messages: llmMessages,
                Tools: allowedToolDefinitions,
                Temperature: agent.Temperature
            );

            var pendingToolCalls = new List<LlmToolCall>();
            var assistantContentAccumulator = new System.Text.StringBuilder();

            // Acquire inference concurrency permit with queue timeout and overload backpressure
            IDisposable? inferencePermit = null;
            InferenceOverloadException? overloadException = null;
            try
            {
                inferencePermit = await _throttlingManager.AcquirePermitAsync(ct);
            }
            catch (InferenceOverloadException ex)
            {
                overloadException = ex;
            }

            if (overloadException != null)
            {
                yield return OverloadEvent(overloadException);
                yield break;
            }

            string? providerError = null;
            try
            {
                await foreach (var chunk in _llmProvider.StreamChatAsync(request, ct))
                {
                    if (chunk.IsError)
                    {
                        providerError = chunk.ErrorMessage ?? "unknown provider error";
                        break;
                    }

                    if (!string.IsNullOrEmpty(chunk.DeltaText))
                    {
                        assistantContentAccumulator.Append(chunk.DeltaText);
                        yield return new ChatEvent("token", chunk.DeltaText);
                    }

                    if (chunk.ToolCalls != null && chunk.ToolCalls.Count > 0)
                    {
                        pendingToolCalls.AddRange(chunk.ToolCalls);
                    }
                }
            }
            finally
            {
                inferencePermit?.Dispose();
            }

            if (providerError != null)
            {
                _logger.LogError("LLM provider failure on first pass: {ProviderError}", providerError);
                yield return ProviderErrorEvent();
                yield break;
            }

            // If the model produced 0 tokens and 0 tool calls while tools were enabled,
            // retry once without tools to produce natural conversational output for general greetings or inquiries.
            // The retry is a second inference call and therefore MUST be admitted through the same throttle:
            // no permit => no upstream call.
            if (assistantContentAccumulator.Length == 0 && pendingToolCalls.Count == 0 && allowedToolDefinitions.Count > 0)
            {
                var directRequest = new LlmChatRequest(
                    Model: agent.ModelName,
                    Messages: llmMessages,
                    Tools: Array.Empty<ToolDefinition>(),
                    Temperature: agent.Temperature
                );

                IDisposable? directPermit = null;
                InferenceOverloadException? retryOverload = null;
                try
                {
                    directPermit = await _throttlingManager.AcquirePermitAsync(ct);
                }
                catch (InferenceOverloadException ex)
                {
                    retryOverload = ex;
                }

                if (retryOverload != null)
                {
                    yield return OverloadEvent(retryOverload);
                    yield break;
                }

                string? retryError = null;
                try
                {
                    await foreach (var chunk in _llmProvider.StreamChatAsync(directRequest, ct))
                    {
                        if (chunk.IsError)
                        {
                            retryError = chunk.ErrorMessage ?? "unknown provider error";
                            break;
                        }
                        if (!string.IsNullOrEmpty(chunk.DeltaText))
                        {
                            assistantContentAccumulator.Append(chunk.DeltaText);
                            yield return new ChatEvent("token", chunk.DeltaText);
                        }
                    }
                }
                finally
                {
                    directPermit?.Dispose();
                }

                if (retryError != null)
                {
                    // Never persist an empty/partial assistant turn nor emit "done" after an upstream failure.
                    _logger.LogError("LLM provider failure on conversational retry: {ProviderError}", retryError);
                    yield return ProviderErrorEvent();
                    yield break;
                }
            }

            // Silence on the very first inference of a turn (no tool has run yet) is a failure, not a success.
            // After tool execution an empty final text is legitimate and handled by the voice route fallback.
            if (iterations == 1 && assistantContentAccumulator.Length == 0 && pendingToolCalls.Count == 0)
            {
                _logger.LogWarning("LLM produced no output for conversation {ConversationId} (tools offered: {ToolCount})",
                    conversation.Id, allowedToolDefinitions.Count);
                yield return new ChatEvent("error", "لم يصدر الوكيل أي رد. حاول إعادة صياغة سؤالك.",
                    new ChatErrorInfo("LLM_EMPTY_RESPONSE", "llm"));
                yield break;
            }

            // Save assistant message to DB
            var assistantText = assistantContentAccumulator.ToString();
            nextSeq++;
            var assistantMsgEntity = new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Role = "assistant",
                Content = assistantText,
                SequenceNumber = nextSeq,
                CreatedAtUtc = DateTime.UtcNow
            };
            _db.Messages.Add(assistantMsgEntity);
            await _db.SaveChangesAsync(ct);

            if (pendingToolCalls.Count > 0)
            {
                llmMessages.Add(new LlmChatMessage("assistant", assistantText, ToolCalls: new List<LlmToolCall>(pendingToolCalls)));
            }
            else
            {
                llmMessages.Add(new LlmChatMessage("assistant", assistantText));
            }

            // If no tools were invoked, conversation turn is completed
            if (pendingToolCalls.Count == 0)
            {
                yield return new ChatEvent("done", "اكتمل الرد بنجاح.");
                yield break;
            }

            // Execute pending tool calls
            foreach (var call in pendingToolCalls)
            {
                yield return new ChatEvent("tool_call", call.Name, new { callId = call.Id, arguments = call.ArgumentsJson });

                // Check authorization against Agent's allowed list
                var isAuthorized = allowedToolNames.Contains(call.Name, StringComparer.OrdinalIgnoreCase);
                if (!isAuthorized)
                {
                    var unauthResult = new ToolResult(
                        Success: false,
                        ErrorCode: "UNAUTHORIZED_TOOL",
                        Message: $"الأداة '{call.Name}' غير مصرح بها لهذا الوكيل."
                    );

                    var unauthJson = JsonSerializer.Serialize(unauthResult);
                    _db.ToolExecutions.Add(new ToolExecution
                    {
                        Id = Guid.NewGuid(),
                        ConversationId = conversation.Id,
                        ToolName = call.Name,
                        ArgumentsJson = call.ArgumentsJson,
                        ResultJson = unauthJson,
                        Status = "Unauthorized",
                        DurationMs = 0,
                        ExecutedAtUtc = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync(ct);

                    nextSeq++;
                    _db.Messages.Add(new Message
                    {
                        Id = Guid.NewGuid(),
                        ConversationId = conversation.Id,
                        Role = "tool",
                        ToolName = call.Name,
                        ToolCallId = call.Id,
                        Content = unauthJson,
                        SequenceNumber = nextSeq,
                        CreatedAtUtc = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync(ct);

                    llmMessages.Add(new LlmChatMessage("tool", unauthJson, ToolCallId: call.Id, ToolName: call.Name));
                    yield return new ChatEvent("tool_result", call.Name, unauthResult);
                    continue;
                }

                // Look up tool implementation
                var tool = _toolRegistry.GetTool(call.Name);
                if (tool == null)
                {
                    var notFoundResult = new ToolResult(
                        Success: false,
                        ErrorCode: "TOOL_NOT_FOUND",
                        Message: $"الأداة '{call.Name}' غير مسجلة في النظام."
                    );
                    var notFoundJson = JsonSerializer.Serialize(notFoundResult);
                    yield return new ChatEvent("tool_result", call.Name, notFoundResult);
                    continue;
                }

                // Parse arguments
                JsonElement argsElement = default;
                ToolResult? parseError = null;
                try
                {
                    using var parsedDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                    argsElement = parsedDoc.RootElement.Clone();
                }
                catch (Exception ex)
                {
                    parseError = new ToolResult(
                        Success: false,
                        ErrorCode: "INVALID_ARGUMENTS",
                        Message: $"صيغة معاملات الأداة غير صحيحة: {ex.Message}"
                    );
                }

                if (parseError != null)
                {
                    yield return new ChatEvent("tool_result", call.Name, parseError);
                    continue;
                }

                // Execute tool with duration timing
                var sw = Stopwatch.StartNew();
                ToolResult executionResult;
                try
                {
                    executionResult = await tool.ExecuteAsync(argsElement, conversation.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Tool execution failed for {ToolName}", call.Name);
                    executionResult = new ToolResult(
                        Success: false,
                        ErrorCode: "EXECUTION_EXCEPTION",
                        Message: $"حدث خطأ أثناء تنفيذ الأداة: {ex.Message}"
                    );
                }
                sw.Stop();

                var resultJson = JsonSerializer.Serialize(executionResult);

                // Record tool audit log in DB
                _db.ToolExecutions.Add(new ToolExecution
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversation.Id,
                    ToolName = call.Name,
                    ArgumentsJson = call.ArgumentsJson,
                    ResultJson = resultJson,
                    Status = executionResult.Success ? "Success" : "Failed",
                    DurationMs = sw.ElapsedMilliseconds,
                    ExecutedAtUtc = DateTime.UtcNow
                });

                // Add tool message to history
                nextSeq++;
                _db.Messages.Add(new Message
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversation.Id,
                    Role = "tool",
                    ToolName = call.Name,
                    ToolCallId = call.Id,
                    Content = resultJson,
                    SequenceNumber = nextSeq,
                    CreatedAtUtc = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(ct);

                llmMessages.Add(new LlmChatMessage("tool", resultJson, ToolCallId: call.Id, ToolName: call.Name));
                yield return new ChatEvent("tool_result", call.Name, executionResult);
            }
        }

        yield return new ChatEvent("done", "اكتملت المحادثة.");
    }
    private static ChatEvent OverloadEvent(InferenceOverloadException ex) =>
        new("error", ex.Message, new ChatErrorInfo(ex.Code, "llm", ex.RetryAfterSeconds));

    private static ChatEvent ProviderErrorEvent() =>
        new("error", "خدمة الذكاء الاصطناعي غير متاحة حالياً أو فشلت في إكمال الرد. حاول مرة أخرى بعد قليل.",
            new ChatErrorInfo("LLM_PROVIDER_UNAVAILABLE", "llm", 5));
}
