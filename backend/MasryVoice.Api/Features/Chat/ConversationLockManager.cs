using System.Collections.Concurrent;

namespace MasryVoice.Api.Features.Chat;

/// <summary>
/// Provides keyed serialization per conversation ID to preserve message ordering
/// and prevent state race conditions within the same conversation, while allowing
/// independent conversations to execute concurrently.
/// </summary>
public class ConversationLockManager
{
    private sealed class RefCountedSemaphore
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount { get; set; } = 1;
    }

    private readonly ConcurrentDictionary<Guid, RefCountedSemaphore> _locks = new();

    public async Task<IDisposable> AcquireLockAsync(Guid conversationId, CancellationToken ct = default)
    {
        RefCountedSemaphore item;
        lock (_locks)
        {
            if (_locks.TryGetValue(conversationId, out var existing))
            {
                existing.RefCount++;
                item = existing;
            }
            else
            {
                item = new RefCountedSemaphore();
                _locks[conversationId] = item;
            }
        }

        try
        {
            await item.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ReleaseRef(conversationId, item, acquired: false);
            throw;
        }

        return new Releaser(this, conversationId, item);
    }

    private void ReleaseRef(Guid conversationId, RefCountedSemaphore item, bool acquired)
    {
        if (acquired)
        {
            item.Semaphore.Release();
        }

        lock (_locks)
        {
            item.RefCount--;
            if (item.RefCount <= 0)
            {
                _locks.TryRemove(conversationId, out _);
                item.Semaphore.Dispose();
            }
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly ConversationLockManager _manager;
        private readonly Guid _conversationId;
        private readonly RefCountedSemaphore _item;
        private int _disposed;

        public Releaser(ConversationLockManager manager, Guid conversationId, RefCountedSemaphore item)
        {
            _manager = manager;
            _conversationId = conversationId;
            _item = item;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _manager.ReleaseRef(_conversationId, _item, acquired: true);
            }
        }
    }
}
