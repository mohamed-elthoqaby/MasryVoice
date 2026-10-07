using System.Collections.Concurrent;

namespace MasryVoice.Api.Features.Voice;

public class VoiceTurnContext : IDisposable
{
    private bool _disposed;
    public long TurnId { get; }
    public CancellationTokenSource Cts { get; }
    public CancellationToken Token => Cts.Token;
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public bool IsDisposed => _disposed;

    public VoiceTurnContext(long turnId)
    {
        TurnId = turnId;
        Cts = new CancellationTokenSource();
    }

    public void CancelSafely()
    {
        if (_disposed) return;
        try
        {
            if (!Cts.IsCancellationRequested)
            {
                Cts.Cancel();
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Cts.Dispose();
        }
    }
}

public class VoiceSession
{
    public Guid SessionId { get; }
    public Guid ConversationId { get; }
    private long _currentTurnId = 0;
    private VoiceTurnContext? _currentTurn;
    private readonly object _lock = new();

    public VoiceSession(Guid sessionId, Guid conversationId)
    {
        SessionId = sessionId;
        ConversationId = conversationId;
    }

    public long CurrentTurnId
    {
        get
        {
            lock (_lock) return _currentTurnId;
        }
    }

    public VoiceTurnContext StartNewTurn()
    {
        lock (_lock)
        {
            // Cancel existing turn if still running
            _currentTurn?.CancelSafely();

            _currentTurnId++;
            _currentTurn = new VoiceTurnContext(_currentTurnId);
            return _currentTurn;
        }
    }

    public bool InterruptCurrentTurn()
    {
        lock (_lock)
        {
            if (_currentTurn != null && !_currentTurn.IsDisposed)
            {
                _currentTurn.CancelSafely();
                _currentTurnId++; // Increment to invalidate any pending synthesis in flight
                return true;
            }
            return false;
        }
    }

    public bool IsTurnActive(long turnId)
    {
        lock (_lock)
        {
            if (_currentTurnId != turnId) return false;
            if (_currentTurn == null || _currentTurn.IsDisposed) return true;
            try
            {
                return !_currentTurn.Cts.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
}

public class VoiceSessionManager
{
    private readonly ConcurrentDictionary<Guid, VoiceSession> _sessions = new();

    public VoiceSession GetOrCreateSession(Guid sessionId, Guid conversationId)
    {
        return _sessions.GetOrAdd(sessionId, id => new VoiceSession(id, conversationId));
    }

    public VoiceSession? GetSession(Guid sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public bool Interrupt(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session.InterruptCurrentTurn();
        }
        return false;
    }

    public void RemoveSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.InterruptCurrentTurn();
        }
    }
}
