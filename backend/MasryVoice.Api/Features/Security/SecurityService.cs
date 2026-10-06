using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MasryVoice.Api.Features.Security;

public interface ISecurityService
{
    bool ValidateAdminKey(string? providedKey);
    string GenerateCustomerToken(Guid conversationId, string? phoneNumber);
    bool ValidateCustomerAccess(string? token, Guid conversationId, string? phoneNumber = null);
    Guid? GetTokenConversationId(string? token);
}

public class SecurityService : ISecurityService
{
    private readonly byte[] _secretKey;
    private readonly string _adminKey;

    public SecurityService(IConfiguration configuration, IWebHostEnvironment? env = null)
    {
        var admin = configuration["Security:AdminKey"];
        var hmac = configuration["Security:HmacSecret"];

        // Reject missing or placeholder production secrets at startup
        if (env != null && env.IsProduction())
        {
            if (string.IsNullOrWhiteSpace(admin) ||
                admin.Contains("YOUR_ADMIN", StringComparison.OrdinalIgnoreCase) ||
                admin.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
                admin.Length < 16)
            {
                throw new InvalidOperationException("Production startup rejected: Security:AdminKey must be a non-placeholder secret with at least 16 characters.");
            }

            if (string.IsNullOrWhiteSpace(hmac) ||
                hmac.Contains("YOUR_HMAC", StringComparison.OrdinalIgnoreCase) ||
                hmac.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
                hmac.Length < 16)
            {
                throw new InvalidOperationException("Production startup rejected: Security:HmacSecret must be a non-placeholder secret with at least 16 characters.");
            }
        }

        _adminKey = admin ?? "masryvoice_admin_secret_dev_2026";
        var secretStr = hmac ?? "masryvoice_customer_hmac_secret_key_dev_2026";
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

    public Guid? GetTokenConversationId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        token = token.Replace(@"\u002b", "+", StringComparison.OrdinalIgnoreCase)
                     .Replace(@"\u002f", "/", StringComparison.OrdinalIgnoreCase)
                     .Replace(@"\u003d", "=", StringComparison.OrdinalIgnoreCase);

        var parts = token.Split('.');
        if (parts.Length != 2) return null;

        var base64Payload = parts[0];
        var providedSignature = parts[1];

        using var hmac = new HMACSHA256(_secretKey);
        var computedSignature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(base64Payload)));

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedSignature),
            Encoding.UTF8.GetBytes(computedSignature)))
        {
            return null;
        }

        try
        {
            var jsonBytes = Convert.FromBase64String(base64Payload);
            var payload = JsonSerializer.Deserialize<CustomerTokenPayload>(jsonBytes);
            if (payload == null || payload.ExpiresAtUtc < DateTime.UtcNow) return null;
            return payload.ConversationId;
        }
        catch
        {
            return null;
        }
    }

    public bool ValidateCustomerAccess(string? token, Guid conversationId, string? phoneNumber = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        token = token.Replace(@"\u002b", "+", StringComparison.OrdinalIgnoreCase)
                     .Replace(@"\u002f", "/", StringComparison.OrdinalIgnoreCase)
                     .Replace(@"\u003d", "=", StringComparison.OrdinalIgnoreCase);

        var parts = token.Split('.');
        if (parts.Length != 2) return null != null;

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
