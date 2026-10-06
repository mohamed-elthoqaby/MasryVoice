namespace MasryVoice.Api.Features.Telephony;

public interface ITelephonyAdapter
{
    Task StartListeningAsync(int port, CancellationToken ct);
    Task StopAsync();
    bool IsListening { get; }
    int ActiveCallsCount { get; }
}

public class TelephonyCallSession
{
    public Guid CallId { get; set; } = Guid.NewGuid();
    public string CallerNumber { get; set; } = string.Empty;
    public string DestinationNumber { get; set; } = string.Empty;
    public DateTime ConnectedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid ConversationId { get; set; }
}
