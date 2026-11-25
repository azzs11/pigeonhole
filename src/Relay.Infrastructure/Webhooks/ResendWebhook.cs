using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Relay.Infrastructure.Webhooks;

/// <summary>Resend signs webhooks with Svix: HMAC-SHA256 over "{id}.{timestamp}.{body}".</summary>
public static class ResendWebhook
{
    public static bool Verify(
        string secret, string? id, string? timestamp, string? signatures, string body, DateTimeOffset now, TimeSpan tolerance)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatures) || !long.TryParse(timestamp, out var unix))
        {
            return false;
        }

        // Replay protection, part 1: old signatures are refused. Part 2 is event-id dedupe.
        if ((now - DateTimeOffset.FromUnixTimeSeconds(unix)).Duration() > tolerance)
        {
            return false;
        }

        var expected = Compute(secret, id, timestamp!, body);
        foreach (var part in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var buffer = new byte[64];
            if (part.StartsWith("v1,", StringComparison.Ordinal)
                && Convert.TryFromBase64String(part[3..], buffer, out var written)
                && CryptographicOperations.FixedTimeEquals(buffer.AsSpan(0, written), expected))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The "v1,..." signature for a payload. Used by tests and tooling.</summary>
    public static string Sign(string secret, string id, string timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return "v1," + Convert.ToBase64String(Compute(secret, id, timestamp, body));
    }

    public static DeliveryEvent Parse(string eventId, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString() ?? string.Empty;
        var data = root.GetProperty("data");
        var emailId = data.TryGetProperty("email_id", out var e) ? e.GetString() : null;
        string[] to = data.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.Array
            ? [.. t.EnumerateArray().Select(x => x.GetString()!).Where(x => x is not null)]
            : [];

        var bounceType = data.TryGetProperty("bounce", out var b) && b.TryGetProperty("type", out var bt) ? bt.GetString() : null;
        var soft = bounceType is not null
            && (bounceType.Equals("Transient", StringComparison.OrdinalIgnoreCase) || bounceType.Contains("soft", StringComparison.OrdinalIgnoreCase));

        var kind = type switch
        {
            "email.sent" => DeliveryEventKind.Sent,
            "email.delivered" => DeliveryEventKind.Delivered,
            "email.bounced" => soft ? DeliveryEventKind.SoftBounce : DeliveryEventKind.HardBounce,
            "email.complained" => DeliveryEventKind.Complaint,
            _ => DeliveryEventKind.Ignored,
        };
        return new DeliveryEvent("resend", eventId, kind, emailId, null, to, bounceType is null ? type : $"{type} ({bounceType})");
    }

    private static byte[] Compute(string secret, string id, string timestamp, string body)
    {
        var key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret);
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));
    }
}
