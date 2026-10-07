namespace MasryVoice.Api.Features.Voice;

/// <summary>
/// Server-side helper to safely validate audio containers and estimate duration
/// before forwarding payloads to neural inference models.
/// </summary>
public static class AudioDurationHelper
{
    /// <summary>
    /// Estimates duration in seconds from standard RIFF PCM WAV headers.
    /// Returns null if format cannot be deterministically inferred.
    /// </summary>
    public static double? EstimateDurationSeconds(ReadOnlySpan<byte> data)
    {
        if (data.Length < 44) return null;

        // Verify standard RIFF WAVE
        if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E')
        {
            int pos = 12;
            int byteRate = 0;
            int dataLength = 0;

            while (pos + 8 <= data.Length)
            {
                var chunkHeader = data.Slice(pos, 4);
                int chunkSize = BitConverter.ToInt32(data.Slice(pos + 4, 4));
                pos += 8;

                if (chunkHeader[0] == (byte)'d' && chunkHeader[1] == (byte)'a' && chunkHeader[2] == (byte)'t' && chunkHeader[3] == (byte)'a')
                {
                    dataLength = chunkSize > 0 ? chunkSize : (data.Length - pos);
                    break;
                }

                if (chunkSize < 0 || pos + chunkSize > data.Length)
                {
                    // Handle malformed or truncated subchunks
                    chunkSize = data.Length - pos;
                }

                if (chunkHeader[0] == (byte)'f' && chunkHeader[1] == (byte)'m' && chunkHeader[2] == (byte)'t' && chunkHeader[3] == (byte)' ')
                {
                    if (chunkSize >= 16 && pos + 12 <= data.Length)
                    {
                        byteRate = BitConverter.ToInt32(data.Slice(pos + 8, 4));
                    }
                }

                pos += chunkSize;
            }

            if (byteRate > 0 && dataLength > 0)
            {
                return (double)dataLength / byteRate;
            }
        }

        return null;
    }
}
