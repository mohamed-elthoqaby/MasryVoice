using System.Diagnostics;

namespace MasryVoice.Api.Features.Chat;

public record InferenceThrottleOptions
{
    public int MaxConcurrentInference { get; init; } = 1; // Default 1 for laptop CPU / single GPU
    public int MaxQueueLength { get; init; } = 5;          // Maximum waiting callers before immediate rejection
    public int QueueWaitTimeoutSeconds { get; init; } = 15; // Maximum seconds in queue before timing out
}

public class InferenceOverloadException : Exception
{
    public string Code { get; }
    public int RetryAfterSeconds { get; }

    public InferenceOverloadException(string message, string code = "INFERENCE_OVERLOAD", int retryAfterSeconds = 5)
        : base(message)
    {
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }
}

/// <summary>
/// Separates expensive LLM inference traffic from ordinary API traffic with bounded concurrency,
/// bounded queue capacity, and explicit overload rejection.
/// </summary>
public class InferenceThrottlingManager
{
    private readonly SemaphoreSlim _semaphore;
    private readonly InferenceThrottleOptions _options;
    private int _waitingCount;
    private int _activeCount;
    private long _totalRejections;
    private long _totalProcessed;

    public InferenceThrottlingManager(InferenceThrottleOptions? options = null)
    {
        _options = options ?? new InferenceThrottleOptions();
        _semaphore = new SemaphoreSlim(_options.MaxConcurrentInference, _options.MaxConcurrentInference);
    }

    public int ActiveCount => Volatile.Read(ref _activeCount);
    public int WaitingCount => Volatile.Read(ref _waitingCount);
    public long TotalRejections => Interlocked.Read(ref _totalRejections);
    public long TotalProcessed => Interlocked.Read(ref _totalProcessed);

    public virtual async Task<IDisposable> AcquirePermitAsync(CancellationToken ct = default)
    {
        // 1. Check if queue is full -> Reject immediately with 429 / overload
        int currentWaiting = Interlocked.Increment(ref _waitingCount);
        if (currentWaiting > _options.MaxQueueLength)
        {
            Interlocked.Decrement(ref _waitingCount);
            Interlocked.Increment(ref _totalRejections);
            throw new InferenceOverloadException(
                "الخادم تحت ضغط استدلال عالٍ حالياً وقائمة الانتظار ممتلئة. يرجى المحاولة بعد قليل.",
                code: "QUEUE_FULL",
                retryAfterSeconds: 5
            );
        }

        var sw = Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.QueueWaitTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await _semaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _waitingCount);
            Interlocked.Increment(ref _totalRejections);
            throw new InferenceOverloadException(
                "انتهت مهلة انتظار معالجة الاستدلال في الطابور نتيجة انشغال المعالج. يرجى المحاولة لاحقاً.",
                code: "QUEUE_TIMEOUT",
                retryAfterSeconds: 10
            );
        }
        catch
        {
            Interlocked.Decrement(ref _waitingCount);
            throw;
        }

        // Successfully entered execution
        Interlocked.Decrement(ref _waitingCount);
        Interlocked.Increment(ref _activeCount);
        sw.Stop();

        return new PermitReleaser(this);
    }

    private void ReleasePermit()
    {
        Interlocked.Decrement(ref _activeCount);
        Interlocked.Increment(ref _totalProcessed);
        _semaphore.Release();
    }

    private sealed class PermitReleaser : IDisposable
    {
        private readonly InferenceThrottlingManager _manager;
        private int _disposed;

        public PermitReleaser(InferenceThrottlingManager manager)
        {
            _manager = manager;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _manager.ReleasePermit();
            }
        }
    }
}
