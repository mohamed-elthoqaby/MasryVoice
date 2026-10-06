using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MasryVoice.Api.Domain;

public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string ModelName { get; set; } = "qwen2.5:1.5b";
    public string LanguageCode { get; set; } = "ar-EG";
    public double Temperature { get; set; } = 0.2;
    public bool IsActive { get; set; } = true;
    public string AllowedToolsJson { get; set; } = "[\"CheckAvailability\",\"StageBooking\",\"GetBooking\"]";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<string> GetAllowedTools()
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(AllowedToolsJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public void SetAllowedTools(IEnumerable<string> tools)
    {
        AllowedToolsJson = JsonSerializer.Serialize(tools);
    }
}

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public string? CustomerPhoneNumber { get; set; }
    public string? CustomerName { get; set; }
    public string Channel { get; set; } = "WebText"; // WebText, WebVoice, SIP
    public string Status { get; set; } = "Active"; // Active, Completed, Cancelled
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastActiveAtUtc { get; set; } = DateTime.UtcNow;

    public Agent Agent { get; set; } = null!;
    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<ToolExecution> ToolExecutions { get; set; } = new List<ToolExecution>();
    public ICollection<PendingBooking> PendingBookings { get; set; } = new List<PendingBooking>();
}

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = "user"; // system, user, assistant, tool
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public int SequenceNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Conversation Conversation { get; set; } = null!;
}

public class ToolExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public string Status { get; set; } = "Success"; // Success, Failed, Unauthorized, InvalidArguments
    public long DurationMs { get; set; }
    public DateTime ExecutedAtUtc { get; set; } = DateTime.UtcNow;

    public Conversation Conversation { get; set; } = null!;
}

public class AvailabilitySlot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ServiceName { get; set; } = "General Consultation";
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public int TotalCapacity { get; set; } = 1;
    public int BookedCapacity { get; set; } = 0;

    public bool IsAvailable => BookedCapacity < TotalCapacity;
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<PendingBooking> PendingBookings { get; set; } = new List<PendingBooking>();
}

public class PendingBooking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Guid SlotId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public DateTime BookingDateUtc { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // Pending, Confirmed, Invalidated, FailedCapacity
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAtUtc { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public AvailabilitySlot Slot { get; set; } = null!;

    public static string ComputeRequestHash(Guid slotId, string customerPhone, string customerName, string serviceName)
    {
        var raw = $"{slotId:N}:{customerPhone.Trim()}:{customerName.Trim().ToLowerInvariant()}:{serviceName.Trim().ToLowerInvariant()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SlotId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public DateTime BookingDateUtc { get; set; }
    public string Status { get; set; } = "Confirmed"; // Confirmed, Cancelled
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public AvailabilitySlot Slot { get; set; } = null!;
}

public class KnowledgeDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public int ChunkCount { get; set; } = 0;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();
}

public class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = string.Empty;
    public Pgvector.Vector? Embedding { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public KnowledgeDocument Document { get; set; } = null!;
}

public class OutboxJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Topic { get; set; } = string.Empty; // e.g. "BookingConfirmed", "SmsReminder"
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, DeadLetter
    public int RetryCount { get; set; } = 0;
    public int MaxRetries { get; set; } = 5;
    public DateTime NextRetryUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public string? LastError { get; set; }
}
