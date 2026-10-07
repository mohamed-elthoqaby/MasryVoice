namespace MasryVoice.Api.Features.Voice;

/// <summary>
/// Server-side helper to safely validate audio containers and enforce duration/integrity bounds
/// before forwarding payloads to neural inference models.
/// </summary>
public static class AudioDurationHelper
{
    public record ValidationResult(bool IsValid, double? DurationSeconds, string? ErrorCode, string? ErrorMessage);

    /// <summary>
    /// Validates audio payload and estimates duration.
    /// Rejects truncated WAV chunks, spoofed headers, and corrupt containers.
    /// </summary>
    public static ValidationResult Validate(ReadOnlySpan<byte> data, double maxDurationSeconds = 30.5)
    {
        if (data.Length < 12)
        {
            return new ValidationResult(false, null, "AUDIO_TRUNCATED_OR_CORRUPT", "ملف الصوت مبتور أو لا يحتوي على ترويسة صالحة.");
        }

        // 1. RIFF WAVE validation
        if (data.Length >= 44 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E')
        {
            int riffSize = BitConverter.ToInt32(data.Slice(4, 4));
            // Check if RIFF chunk size is grossly larger than the actual payload (truncated file)
            if (riffSize + 8 > data.Length + 1024)
            {
                return new ValidationResult(false, null, "AUDIO_TRUNCATED_OR_CORRUPT", "ملف WAV مبتور؛ حجم الملف الفعلي أقل من الحجم المعلن في الترويسة.");
            }

            int pos = 12;
            int byteRate = 0;
            int dataLength = 0;
            bool foundFmt = false;
            bool foundData = false;

            while (pos + 8 <= data.Length)
            {
                var chunkHeader = data.Slice(pos, 4);
                int chunkSize = BitConverter.ToInt32(data.Slice(pos + 4, 4));
                pos += 8;

                if (chunkSize < 0)
                {
                    return new ValidationResult(false, null, "AUDIO_TRUNCATED_OR_CORRUPT", "حجم مقطع WAV غير صالح.");
                }

                if (chunkHeader[0] == (byte)'f' && chunkHeader[1] == (byte)'m' && chunkHeader[2] == (byte)'t' && chunkHeader[3] == (byte)' ')
                {
                    foundFmt = true;
                    if (chunkSize >= 16 && pos + 12 <= data.Length)
                    {
                        byteRate = BitConverter.ToInt32(data.Slice(pos + 8, 4));
                    }
                }
                else if (chunkHeader[0] == (byte)'d' && chunkHeader[1] == (byte)'a' && chunkHeader[2] == (byte)'t' && chunkHeader[3] == (byte)'a')
                {
                    foundData = true;
                    dataLength = chunkSize;
                    int actualRemainingBytes = data.Length - pos;
                    // Check for spoofed or truncated data chunk
                    if (actualRemainingBytes < dataLength - 32)
                    {
                        return new ValidationResult(false, null, "AUDIO_TRUNCATED_OR_CORRUPT", "مقطع بيانات WAV مبتور؛ حجم البيانات الفعلي لا يطابق الترويسة.");
                    }
                    break;
                }

                pos += chunkSize;
            }

            if (!foundFmt || !foundData || byteRate <= 0)
            {
                return new ValidationResult(false, null, "AUDIO_TRUNCATED_OR_CORRUPT", "ترويسة WAV غير مكتملة أو تفتقر لمقاطع fmt و data.");
            }

            double duration = (double)dataLength / byteRate;
            if (duration > maxDurationSeconds)
            {
                return new ValidationResult(false, duration, "AUDIO_DURATION_EXCEEDED", $"مدة التسجيل الصوتي ({duration:F1} ثانية) تتجاوز الحد الأقصى المسموح به وهو 30 ثانية.");
            }

            return new ValidationResult(true, duration, null, null);
        }

        // 2. WebM container basic check (EBML header 0x1A 0x45 0xDF 0xA3)
        if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
        {
            return new ValidationResult(true, null, null, null);
        }

        // 3. Ogg container (0x4F 0x67 0x67 0x53 "OggS")
        if (data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53)
        {
            return new ValidationResult(true, null, null, null);
        }

        // Neither WAV, WebM, nor Ogg
        return new ValidationResult(false, null, "AUDIO_TRUNCATED_OR_CORRUPT", "تنسيق حاوية الصوت غير مدعوم أو تالف.");
    }

    /// <summary>
    /// Estimates duration in seconds from standard RIFF PCM WAV headers.
    /// Returns null if format cannot be deterministically inferred.
    /// </summary>
    public static double? EstimateDurationSeconds(ReadOnlySpan<byte> data)
    {
        var result = Validate(data);
        return result.DurationSeconds;
    }
}
