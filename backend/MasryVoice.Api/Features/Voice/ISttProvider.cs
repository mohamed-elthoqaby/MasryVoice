using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Voice;

public interface ISttProvider
{
    Task<string> TranscribeAudioAsync(Stream audioStream, string contentType = "audio/wav", string language = "ar", CancellationToken ct = default);
}

/// <summary>
/// Production Whisper STT Provider calling an OpenAI-compatible speech transcription endpoint
/// (e.g., faster-whisper, whisper.cpp, or local Whisper server).
/// Strictly fails when the real endpoint is unavailable or returns errors.
/// Never fabricates synthetic transcripts on failure.
/// </summary>
public class WhisperSttProvider : ISttProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WhisperSttProvider> _logger;

    public WhisperSttProvider(HttpClient httpClient, ILogger<WhisperSttProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> TranscribeAudioAsync(Stream audioStream, string contentType = "audio/wav", string language = "ar", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (audioStream == null || (audioStream.CanSeek && audioStream.Length == 0))
        {
            throw new ArgumentException("Audio stream cannot be null or empty.", nameof(audioStream));
        }

        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(audioStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var ext = contentType.Contains("webm", StringComparison.OrdinalIgnoreCase) ? ".webm"
            : (contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase) ? ".ogg" : ".wav");
        content.Add(streamContent, "file", $"audio{ext}");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent(language), "language");
        content.Add(new StringContent("عيادة النور التخصصية، حجز كشف باطنة، أطفال، عظام، دكتور، مواعيد، تأكيد الحجز، الاسم، رقم التليفون صفر واحد اثنان ثلاثة اربعة خمسة ستة سبعة ثمانية تسعة"), "prompt");

        var response = await _httpClient.PostAsync("/v1/audio/transcriptions", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Whisper STT request failed with status code {StatusCode}: {Error}", (int)response.StatusCode, err);
            throw new HttpRequestException($"Whisper STT service returned HTTP {(int)response.StatusCode}: {err}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("text", out var textProp))
        {
            var transcript = textProp.GetString();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                return NormalizeSpokenDigits(transcript);
            }
        }

        throw new InvalidOperationException("Whisper STT response did not contain a valid 'text' transcription field.");
    }

    private static string NormalizeSpokenDigits(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var map = new Dictionary<string, string>
        {
            ["صفر"] = "0", ["سفر"] = "0", ["صف"] = "0", ["الصفر"] = "0", ["السفر"] = "0",
            ["واحد"] = "1", ["واح"] = "1", ["الواحد"] = "1", ["صفواح"] = "01",
            ["اثنان"] = "2", ["اثنين"] = "2", ["تنين"] = "2", ["يثنان"] = "2", ["دثنان"] = "2", ["ثنان"] = "2", ["الاثنين"] = "2", ["الاتنين"] = "2",
            ["ثلاثة"] = "3", ["تلاتة"] = "3", ["ثلاث"] = "3", ["تلات"] = "3", ["الثلاثة"] = "3",
            ["أربعة"] = "4", ["اربعة"] = "4", ["أربع"] = "4", ["اربع"] = "4", ["اربعا"] = "4", ["أربعا"] = "4", ["الاربعة"] = "4", ["الأربعة"] = "4",
            ["خمسة"] = "5", ["كمسة"] = "5", ["خمس"] = "5", ["كامس"] = "5", ["كمس"] = "5", ["الخمسة"] = "5",
            ["ستة"] = "6", ["ست"] = "6", ["سست"] = "6", ["الستة"] = "6",
            ["سبعة"] = "7", ["سبع"] = "7", ["تسبع"] = "7", ["السبعة"] = "7",
            ["ثمانية"] = "8", ["تمانية"] = "8", ["ثماني"] = "8", ["تماني"] = "8", ["الثمانية"] = "8",
            ["تسعة"] = "9", ["تسع"] = "9", ["التسعة"] = "9"
        };
        var tokens = System.Text.RegularExpressions.Regex.Split(text, @"(\s+)");
        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < tokens.Length)
        {
            var tok = tokens[i].Trim();
            var clean = System.Text.RegularExpressions.Regex.Replace(tok, @"[^\w]", "");
            if (map.ContainsKey(clean))
            {
                var digits = new System.Text.StringBuilder();
                int j = i;
                while (j < tokens.Length)
                {
                    var sub = tokens[j].Trim();
                    if (string.IsNullOrEmpty(sub)) { j++; continue; }
                    var cleanSub = System.Text.RegularExpressions.Regex.Replace(sub, @"[^\w]", "");
                    if (map.TryGetValue(cleanSub, out var d))
                    {
                        digits.Append(d);
                        j++;
                    }
                    else break;
                }
                if (digits.Length >= 3)
                {
                    result.Append(digits);
                    i = j;
                    continue;
                }
            }
            result.Append(tokens[i]);
            i++;
        }
        var str = result.ToString();
        return System.Text.RegularExpressions.Regex.Replace(str, @"\b(بسم|بسمي)\b(?=\s+[أ-ي])", "باسم");
    }
}

/// <summary>
/// Explicitly selected Simulated/Demo STT Provider for headless CI runs and offline test environments.
/// Returns predictable Egyptian Arabic test transcriptions without external service dependencies.
/// </summary>
public class SimulatedSttProvider : ISttProvider
{
    private readonly string? _cannedTranscript;

    public SimulatedSttProvider(string? cannedTranscript = null)
    {
        _cannedTranscript = cannedTranscript;
    }

    public Task<string> TranscribeAudioAsync(Stream audioStream, string contentType = "audio/wav", string language = "ar", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(_cannedTranscript))
        {
            return Task.FromResult(_cannedTranscript);
        }

        // Differentiate fixtures deterministically based on fixture payload length
        // turn1_inquiry.wav is ~176KB; turn2_booking.wav is ~374KB
        if (audioStream.Length > 250000)
        {
            return Task.FromResult("احجزلي ميعاد بكرة باسم محمد عاطف ورقمي 01012345678");
        }

        return Task.FromResult("عايز أعرف المواعيد المتاحة بكرة للكشف يا سارة");
    }
}
