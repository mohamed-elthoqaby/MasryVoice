using System.Net;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace MasryVoice.Api.Features.Voice;

/// <summary>
/// Safe, user-facing error envelope for voice/chat pipeline failures.
/// Never carries stack traces, provider URLs, transcripts or exception messages.
/// </summary>
public sealed record VoiceFailure(
    string Stage,
    string Code,
    int StatusCode,
    string Message,
    int? RetryAfterSeconds = null);

public static class VoiceFailureClassifier
{
    /// <summary>
    /// Maps a pipeline exception to a stable (stage, code, HTTP status, Arabic message) tuple.
    /// <paramref name="callerCancelled"/> must be true only when the caller (client disconnect / barge-in)
    /// requested cancellation; any other cancellation is a provider timeout (504), never a fake 499.
    /// </summary>
    public static VoiceFailure Classify(Exception ex, string stage, bool callerCancelled = false)
    {
        switch (ex)
        {
            case VoiceOverloadException ov:
                return new VoiceFailure(stage, ov.Code, StatusCodes.Status429TooManyRequests, ov.Message, ov.RetryAfterSeconds);

            case OperationCanceledException when !callerCancelled:
                return new VoiceFailure(stage, $"{stage.ToUpperInvariant()}_TIMEOUT", StatusCodes.Status504GatewayTimeout,
                    stage == "stt" ? "انتهت مهلة خدمة التعرف على الصوت. حاول مرة أخرى."
                    : stage == "tts" ? "انتهت مهلة خدمة تحويل النص إلى صوت. حاول مرة أخرى."
                    : "انتهت مهلة معالجة الطلب. حاول مرة أخرى.");

            case ArgumentException when stage == "stt":
                return new VoiceFailure(stage, "STT_EMPTY_AUDIO", StatusCodes.Status400BadRequest,
                    "لم يصل أي صوت للتعرف عليه. سجّل مرة أخرى.");

            case ArgumentException when stage == "tts":
                return new VoiceFailure(stage, "TTS_EMPTY_TEXT", StatusCodes.Status400BadRequest,
                    "لا يوجد نص لتحويله إلى صوت.");

            case InvalidOperationException when stage == "stt":
                return new VoiceFailure(stage, "STT_EMPTY_TRANSCRIPT", StatusCodes.Status422UnprocessableEntity,
                    "لم يتم التقاط كلام واضح من التسجيل. تحدث بوضوح وأعد المحاولة.");

            case InvalidOperationException when stage == "tts":
                return new VoiceFailure(stage, "TTS_EMPTY_AUDIO", StatusCodes.Status502BadGateway,
                    "خدمة تحويل النص إلى صوت أعادت ملفاً فارغاً.");

            case HttpRequestException http when http.StatusCode is { } sc:
                // Provider answered: 4xx = it rejected our input (e.g. undecodable container), 5xx = provider fault
                if ((int)sc >= 400 && (int)sc < 500)
                {
                    return stage == "stt"
                        ? new VoiceFailure(stage, "STT_AUDIO_UNDECODABLE", StatusCodes.Status422UnprocessableEntity,
                            "تعذر قراءة الملف الصوتي. سجّل مرة أخرى.")
                        : new VoiceFailure(stage, $"{stage.ToUpperInvariant()}_REQUEST_REJECTED", StatusCodes.Status422UnprocessableEntity,
                            "رفضت الخدمة الطلب.");
                }
                return new VoiceFailure(stage, $"{stage.ToUpperInvariant()}_PROVIDER_ERROR", StatusCodes.Status502BadGateway,
                    stage == "stt" ? "حدث خطأ في خدمة التعرف على الصوت."
                    : "حدث خطأ في خدمة تحويل النص إلى صوت.");

            case HttpRequestException:
                // No HTTP status: connection refused / DNS / reset => provider not reachable
                return new VoiceFailure(stage, $"{stage.ToUpperInvariant()}_PROVIDER_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable,
                    stage == "stt" ? "خدمة التعرف على الصوت غير متاحة حالياً."
                    : "خدمة تحويل النص إلى صوت غير متاحة حالياً.", 5);

            case DbUpdateException:
                return new VoiceFailure("db", "DATABASE_ERROR", StatusCodes.Status500InternalServerError,
                    "تعذر حفظ بيانات المحادثة. حاول مرة أخرى.");

            default:
                return new VoiceFailure(stage, "INTERNAL_ERROR", StatusCodes.Status500InternalServerError,
                    "حدث خطأ غير متوقع في الخادم.");
        }
    }

    /// <summary>Maps an orchestrator error code to a pipeline failure.</summary>
    public static VoiceFailure FromChatError(string code, string stage, string message, int? retryAfter) => code switch
    {
        "QUEUE_FULL" or "QUEUE_TIMEOUT" or "INFERENCE_OVERLOAD" =>
            new VoiceFailure(stage, code, StatusCodes.Status429TooManyRequests, message, retryAfter ?? 5),
        "LLM_PROVIDER_UNAVAILABLE" =>
            new VoiceFailure(stage, code, StatusCodes.Status503ServiceUnavailable, message, retryAfter ?? 5),
        "LLM_EMPTY_RESPONSE" =>
            new VoiceFailure(stage, code, StatusCodes.Status502BadGateway, message),
        "AGENT_NOT_FOUND" =>
            new VoiceFailure("agent", code, StatusCodes.Status404NotFound, message),
        _ => new VoiceFailure(stage, code, StatusCodes.Status500InternalServerError, message)
    };
}

public static class VoiceErrorResults
{
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>
    /// Produces the safe JSON envelope { code, stage, correlationId, message, retryAfterSeconds? }.
    /// The server logs the exception (type + stage + correlation id) but never returns it to the client.
    /// </summary>
    public static IResult Respond(HttpContext ctx, ILogger logger, VoiceFailure failure, Exception? ex = null)
    {
        var correlationId = ctx.TraceIdentifier;
        ctx.Response.Headers[CorrelationHeader] = correlationId;
        if (failure.RetryAfterSeconds is { } ra)
        {
            ctx.Response.Headers.RetryAfter = ra.ToString();
        }

        if (failure.StatusCode >= 500)
        {
            logger.LogError(ex, "Voice pipeline failure stage={Stage} code={Code} status={Status} correlationId={CorrelationId}",
                failure.Stage, failure.Code, failure.StatusCode, correlationId);
        }
        else
        {
            logger.LogWarning("Voice pipeline rejection stage={Stage} code={Code} status={Status} correlationId={CorrelationId} exception={ExceptionType}",
                failure.Stage, failure.Code, failure.StatusCode, correlationId, ex?.GetType().Name);
        }

        return Results.Json(new VoiceErrorEnvelope(failure.Code, failure.Stage, correlationId, failure.Message, failure.RetryAfterSeconds),
            statusCode: failure.StatusCode);
    }
}

public sealed record VoiceErrorEnvelope(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryAfterSeconds")] int? RetryAfterSeconds = null);
