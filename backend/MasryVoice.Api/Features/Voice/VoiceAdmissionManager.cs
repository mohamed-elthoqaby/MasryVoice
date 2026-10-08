using System.Diagnostics;

namespace MasryVoice.Api.Features.Voice;

public record VoiceAdmissionOptions
{
    public int MaxConcurrentStt { get; init; } = 2; // Laptop CPU budget
    public int MaxConcurrentTts { get; init; } = 2;
    public int MaxQueueLength { get; init; } = 5;
    public int QueueWaitTimeoutSeconds { get; init; } = 15;
}

public class VoiceOverloadException : Exception
{
    public string Code { get; }
    public int RetryAfterSeconds { get; }

    public VoiceOverloadException(string message, string code = "VOICE_OVERLOAD", int retryAfterSeconds = 5)
        : base(message)
    {
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }
}

/// <summary>
/// Enforces bounded concurrency and admission control for speech tasks (STT and TTS)
/// separately from LLM inference, preventing thread exhaustion on client machines
/// and guaranteeing zero nested-semaphore deadlocks.
/// </summary>
public class VoiceAdmissionManager
{
    private readonly SemaphoreSlim _sttSemaphore;
    private readonly SemaphoreSlim _ttsSemaphore;
    private readonly VoiceAdmissionOptions _options;
    private int _sttWaiting;
    private int _ttsWaiting;
    private int _sttActive;
    private int _ttsActive;

    public VoiceAdmissionManager(VoiceAdmissionOptions? options = null)
    {
        _options = options ?? new VoiceAdmissionOptions();
        _sttSemaphore = new SemaphoreSlim(_options.MaxConcurrentStt, _options.MaxConcurrentStt);
        _ttsSemaphore = new SemaphoreSlim(_options.MaxConcurrentTts, _options.MaxConcurrentTts);
    }

    public int ActiveSttCount => Volatile.Read(ref _sttActive);
    public int ActiveTtsCount => Volatile.Read(ref _ttsActive);
    public int WaitingSttCount => Volatile.Read(ref _sttWaiting);
    public int WaitingTtsCount => Volatile.Read(ref _ttsWaiting);

    public async Task<IDisposable> AcquireSttPermitAsync(CancellationToken ct = default)
    {
        int waiting = Interlocked.Increment(ref _sttWaiting);
        if (waiting > _options.MaxQueueLength)
        {
            Interlocked.Decrement(ref _sttWaiting);
            throw new VoiceOverloadException(
                "طابور معالجة الصوت (STT) ممتلئ حالياً. يرجى الانتظار قليلاً.",
                code: "STT_QUEUE_FULL",
                retryAfterSeconds: 5
            );
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.QueueWaitTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await _sttSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _sttWaiting);
            throw new VoiceOverloadException(
                "انتهت مهلة انتظار معالجة الصوت (STT) في الطابور.",
                code: "STT_QUEUE_TIMEOUT",
                retryAfterSeconds: 5
            );
        }
        catch
        {
            Interlocked.Decrement(ref _sttWaiting);
            throw;
        }

        Interlocked.Decrement(ref _sttWaiting);
        Interlocked.Increment(ref _sttActive);

        return new SttPermitReleaser(this);
    }

    public async Task<IDisposable> AcquireTtsPermitAsync(CancellationToken ct = default)
    {
        int waiting = Interlocked.Increment(ref _ttsWaiting);
        if (waiting > _options.MaxQueueLength)
        {
            Interlocked.Decrement(ref _ttsWaiting);
            throw new VoiceOverloadException(
                "طابور توليد النطق الصوتي (TTS) ممتلئ حالياً. يرجى الانتظار قليلاً.",
                code: "TTS_QUEUE_FULL",
                retryAfterSeconds: 5
            );
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.QueueWaitTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await _ttsSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _ttsWaiting);
            throw new VoiceOverloadException(
                "انتهت مهلة انتظار توليد النطق الصوتي (TTS) في الطابور.",
                code: "TTS_QUEUE_TIMEOUT",
                retryAfterSeconds: 5
            );
        }
        catch
        {
            Interlocked.Decrement(ref _ttsWaiting);
            throw;
        }

        Interlocked.Decrement(ref _ttsWaiting);
        Interlocked.Increment(ref _ttsActive);

        return new TtsPermitReleaser(this);
    }

    private void ReleaseStt()
    {
        Interlocked.Decrement(ref _sttActive);
        _sttSemaphore.Release();
    }

    private void ReleaseTts()
    {
        Interlocked.Decrement(ref _ttsActive);
        _ttsSemaphore.Release();
    }

    private sealed class SttPermitReleaser : IDisposable
    {
        private readonly VoiceAdmissionManager _mgr;
        private int _disposed;
        public SttPermitReleaser(VoiceAdmissionManager mgr) => _mgr = mgr;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _mgr.ReleaseStt();
        }
    }

    private sealed class TtsPermitReleaser : IDisposable
    {
        private readonly VoiceAdmissionManager _mgr;
        private int _disposed;
        public TtsPermitReleaser(VoiceAdmissionManager mgr) => _mgr = mgr;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _mgr.ReleaseTts();
        }
    }
}
