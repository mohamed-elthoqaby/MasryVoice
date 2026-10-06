using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MasryVoice.Api.Features.Tools;

namespace MasryVoice.Api.Infrastructure.Providers;

public record LlmChatMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    string? ToolName = null,
    List<LlmToolCall>? ToolCalls = null
);

public record LlmToolCall(
    string Id,
    string Name,
    string ArgumentsJson
);

public record LlmChatRequest(
    string Model,
    List<LlmChatMessage> Messages,
    IEnumerable<ToolDefinition> Tools,
    double Temperature = 0.2
);

public record LlmStreamChunk(
    string? DeltaText = null,
    List<LlmToolCall>? ToolCalls = null,
    bool IsCompleted = false,
    string? FinishReason = null,
    bool IsError = false,
    string? ErrorMessage = null
);

public interface ILlmProvider
{
    string ProviderName { get; }
    IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(LlmChatRequest request, CancellationToken ct = default);
}

/// <summary>
/// Explicitly labeled Deterministic Fake LLM Provider for unit testing and offline demo mode only.
/// Strictly follows the StageBooking -> ConfirmBooking lifecycle without relying on model-side confirmation flags.
/// </summary>
public class DeterministicFakeLlmProvider : ILlmProvider
{
    public string ProviderName => "DeterministicFake [DEMO MODE / TEST ONLY]";

    public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastMessage = request.Messages.LastOrDefault();
        var lastUserMsg = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        // 1. If previous step returned tool results, synthesize the Egyptian Arabic answer
        if (lastMessage?.Role == "tool")
        {
            string reply;
            if (lastMessage.ToolName == "CheckAvailability")
            {
                reply = "يا فندم شيكت لحضرتك على المواعيد المتاحة في عيادة النور: متاح كشف باطنة الساعة 10:00 صباحاً والساعة 02:00 مساءً بتوقيت القاهرة. تحب حضرتك أجهز لحجز أي موعد فيهم؟";
            }
            else if (lastMessage.ToolName == "StageBooking")
            {
                reply = "تمام يا فندم، جهزت لحضرتك مسودة الحجز كشف باطنة باسم " + (ExtractName(lastUserMsg) ?? "محمد عاطف") + ". تفاصيل الحجز ظاهرة قدام حضرتك في البطاقة، برجاء مراجعتها والضغط على زر 'تأكيد الحجز' لإتمام التثبيت.";
            }
            else if (lastMessage.ToolName == "GetBooking")
            {
                reply = "لقيت لحضرتك بيانات الحجز في السيستم يا فندم. الميعاد مسجل ومؤكد. تحب أساعد حضرتك بأي تفاصيل تانية؟";
            }
            else
            {
                reply = "تمام يا فندم، راجعت البيانات مع السيستم. تؤمرني بأي خدمة تانية؟";
            }

            foreach (var token in SplitToTokens(reply))
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LlmStreamChunk(DeltaText: token);
            }

            yield return new LlmStreamChunk(IsCompleted: true, FinishReason: "stop");
            yield break;
        }

        // 2. Analyze user Egyptian Arabic intent
        var lower = lastUserMsg.Trim().ToLowerInvariant();

        // Intent A: Check availability ("مواعيد", "ميعاد", "فاضي", "متاح")
        if (lower.Contains("ميعاد") || lower.Contains("مواعيد") || lower.Contains("فاضي") || lower.Contains("متاح") || lower.Contains("إمكانية"))
        {
            var hasCheckTool = request.Tools.Any(t => t.Name.Equals("CheckAvailability", StringComparison.OrdinalIgnoreCase));
            if (hasCheckTool)
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
        }

        // Intent B: Explicit confirmation intent ("أكد", "اكد", "موافق")
        // Directs the customer to the explicit UI confirmation button (trust boundary preserved)
        if (lower.Contains("أكد") || lower.Contains("اكد") || lower.Contains("موافق") || (lower.Contains("ايوة") && !lower.Contains("باسم")))
        {
            var confirmReminder = "لتأكيد الحجز وتثبيته نهائياً يا فندم، يرجى الضغط على زر 'تأكيد الحجز' الظاهر في بطاقة الحجز المعروضة على الشاشة أمامك.";
            foreach (var token in SplitToTokens(confirmReminder))
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LlmStreamChunk(DeltaText: token);
            }
            yield return new LlmStreamChunk(IsCompleted: true, FinishReason: "stop");
            yield break;
        }

        // Intent C: Provide booking details -> Invokes StageBooking (stages pending booking)
        if (lower.Contains("احجز") || lower.Contains("حجز") || lower.Contains("باسم") || lower.Contains("01") || lower.Contains("الساعة") || lower.Contains("ساعة"))
        {
            var hasStageTool = request.Tools.Any(t => t.Name.Equals("StageBooking", StringComparison.OrdinalIgnoreCase));
            if (hasStageTool)
            {
                var customerName = ExtractName(lastUserMsg) ?? "محمد عاطف";
                var customerPhone = ExtractPhone(lastUserMsg) ?? "01012345678";

                yield return new LlmStreamChunk(
                    ToolCalls: new List<LlmToolCall>
                    {
                        new($"call_{Guid.NewGuid():N}", "StageBooking", JsonSerializer.Serialize(new
                        {
                            customerName,
                            customerPhone,
                            serviceName = "كشف باطنة عامة"
                        }))
                    },
                    IsCompleted: true,
                    FinishReason: "tool_calls"
                );
                yield break;
            }
        }

        // Default greeting
        var welcome = "أهلاً بحضرتك في عيادة النور التخصصية! معاك سارة. أقدر أساعدك تستفسر عن المواعيد المتاحة أو نجهز لحجز كشف. تحب أساعدك إزاي النهاردة؟";
        foreach (var token in SplitToTokens(welcome))
        {
            if (ct.IsCancellationRequested) yield break;
            yield return new LlmStreamChunk(DeltaText: token);
        }
        yield return new LlmStreamChunk(IsCompleted: true, FinishReason: "stop");
    }

    private static IEnumerable<string> SplitToTokens(string text)
    {
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            yield return (i == 0 ? "" : " ") + words[i];
        }
    }

    private static string? ExtractName(string text)
    {
        var idx = text.IndexOf("باسم", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var part = text[(idx + 4)..].Trim();
            var parts = part.Split(new[] { ' ', '،', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return $"{parts[0]} {parts[1]}";
            if (parts.Length == 1) return parts[0];
        }
        return null;
    }

    private static string? ExtractPhone(string text)
    {
        foreach (var part in text.Split(new[] { ' ', '،', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("01") && part.Length >= 10 && part.All(char.IsDigit))
            {
                return part;
            }
        }
        return null;
    }
}

/// <summary>
/// Real Ollama LLM Provider calling Ollama REST API with genuine upstream streaming and OpenAI-compatible tool calling.
/// If Ollama is unavailable or fails, it produces a clear error instead of a simulated response.
/// </summary>
public class OllamaLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public string ProviderName => "Ollama";

    public OllamaLlmProvider(HttpClient http, IConfiguration config)
    {
        _http = http;
        _baseUrl = config["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{_baseUrl.TrimEnd('/')}/api/chat";

        var formattedMessages = request.Messages.Select(m =>
        {
            var dict = new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = m.Content ?? ""
            };

            if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                dict["tool_calls"] = m.ToolCalls.Select(tc =>
                {
                    object argsObj;
                    try
                    {
                        argsObj = JsonSerializer.Deserialize<JsonElement>(tc.ArgumentsJson);
                    }
                    catch
                    {
                        argsObj = new { };
                    }

                    return new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new
                        {
                            name = tc.Name,
                            arguments = argsObj
                        }
                    };
                }).ToList();
            }

            return dict;
        }).ToList();

        var toolsPayload = request.Tools.Select(t => t.ToOpenAiFunctionSchema()).ToList();

        var body = new
        {
            model = request.Model,
            messages = formattedMessages,
            stream = true,
            options = new
            {
                temperature = request.Temperature,
                num_predict = 200
            },
            tools = toolsPayload.Count > 0 ? toolsPayload : null
        };

        var json = JsonSerializer.Serialize(body);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage? response = null;
        string? failureError = null;

        try
        {
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                failureError = $"[خطأ Ollama: كود الحالة {response.StatusCode} - تعذر إتمام الاستدلال بالنموذج]";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            yield break;
        }
        catch (Exception ex)
        {
            failureError = $"[خطأ اتصال بخادم Ollama: {ex.Message}. يرجى التأكد من تشغيل Ollama]";
        }

        if (failureError != null)
        {
            // Do NOT fall back to fake success; yield clear error
            yield return new LlmStreamChunk(
                DeltaText: failureError,
                IsCompleted: true,
                IsError: true,
                ErrorMessage: failureError,
                FinishReason: "error"
            );
            yield break;
        }

        using var stream = await response!.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Genuine upstream streaming: yield tokens as soon as Ollama delivers each line
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("message", out var msgElement))
            {
                if (msgElement.TryGetProperty("content", out var contentElem) && !string.IsNullOrEmpty(contentElem.GetString()))
                {
                    yield return new LlmStreamChunk(DeltaText: contentElem.GetString());
                }

                if (msgElement.TryGetProperty("tool_calls", out var toolCallsElem) && toolCallsElem.ValueKind == JsonValueKind.Array)
                {
                    var calls = new List<LlmToolCall>();
                    foreach (var call in toolCallsElem.EnumerateArray())
                    {
                        var func = call.GetProperty("function");
                        var name = func.GetProperty("name").GetString() ?? "";
                        var argsJson = func.TryGetProperty("arguments", out var argsElem) 
                            ? (argsElem.ValueKind == JsonValueKind.String ? argsElem.GetString()! : argsElem.GetRawText())
                            : "{}";

                        calls.Add(new LlmToolCall(
                            Id: $"call_{Guid.NewGuid():N}",
                            Name: name,
                            ArgumentsJson: argsJson
                        ));
                    }

                    if (calls.Count > 0)
                    {
                        yield return new LlmStreamChunk(ToolCalls: calls);
                    }
                }
            }

            if (root.TryGetProperty("done", out var doneElem) && doneElem.GetBoolean())
            {
                yield return new LlmStreamChunk(IsCompleted: true, FinishReason: "stop");
                yield break;
            }
        }
    }
}
