using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MasryVoice.Api.Features.Security;

public interface ISecurityService
{
    bool ValidateAdminKey(string? providedKey);
    string GenerateCustomerToken(Guid conversationId, string? phoneNumber);
    bool ValidateCustomerAccess(string? token, Guid conversationId, string? phoneNumber = null);
}

public class SecurityService : ISecurityService
{
    private readonly byte[] _secretKey;
    private readonly string _adminKey;

    public SecurityService(IConfiguration configuration)
    {
        _adminKey = configuration["Security:AdminKey"] ?? "masryvoice_admin_secret_2026";
        var secretStr = configuration["Security:HmacSecret"] ?? "masryvoice_customer_hmac_secret_key_secure_2026_cairo";
        _secretKey = Encoding.UTF8.GetBytes(secretStr);
    }

    public bool ValidateAdminKey(string? providedKey)
    {
        if (string.IsNullOrWhiteSpace(providedKey)) return false;

        // Strip "Bearer " prefix if provided
        if (providedKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            providedKey = providedKey.Substring(7).Trim();
        }

        // Constant time comparison to prevent timing attacks
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedKey),
            Encoding.UTF8.GetBytes(_adminKey)
        );
    }

    public string GenerateCustomerToken(Guid conversationId, string? phoneNumber)
    {
        var payload = new CustomerTokenPayload
        {
            ConversationId = conversationId,
            PhoneNumber = phoneNumber ?? string.Empty,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
        };

        var json = JsonSerializer.Serialize(payload);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var base64Payload = Convert.ToBase64String(jsonBytes);

        using var hmac = new HMACSHA256(_secretKey);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(base64Payload)));

        return $"{base64Payload}.{signature}";
    }

    public bool ValidateCustomerAccess(string? token, Guid conversationId, string? phoneNumber = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 2) return false;

        var base64Payload = parts[0];
        var providedSignature = parts[1];

        using var hmac = new HMACSHA256(_secretKey);
        var computedSignature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(base64Payload)));

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedSignature),
            Encoding.UTF8.GetBytes(computedSignature)))
        {
            return false;
        }

        try
        {
            var jsonBytes = Convert.FromBase64String(base64Payload);
            var payload = JsonSerializer.Deserialize<CustomerTokenPayload>(jsonBytes);
            if (payload == null) return false;

            if (payload.ExpiresAtUtc < DateTime.UtcNow) return false;
            if (conversationId != Guid.Empty && payload.ConversationId != conversationId) return false;

            if (!string.IsNullOrEmpty(phoneNumber))
            {
                if (string.IsNullOrEmpty(payload.PhoneNumber) ||
                    !string.Equals(payload.PhoneNumber.Trim(), phoneNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private class CustomerTokenPayload
    {
        public Guid ConversationId { get; set; }
        public string PhoneNumber { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }
}
