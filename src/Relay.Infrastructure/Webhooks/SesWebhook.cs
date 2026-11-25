using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Relay.Infrastructure.Webhooks;

// URLs stay strings: they are part of the signed canonical text and must not be normalized.
#pragma warning disable CA1056
/// <summary>An Amazon SNS HTTP message. SES events arrive as the JSON string in <see cref="Message"/>.</summary>
public sealed record SnsEnvelope
{
    public string Type { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public string TopicArn { get; init; } = string.Empty;
    public string? Subject { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Timestamp { get; init; } = string.Empty;
    public string SignatureVersion { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;

    [JsonPropertyName("SigningCertURL")]
    public string SigningCertUrl { get; init; } = string.Empty;

    [JsonPropertyName("SubscribeURL")]
    public string? SubscribeUrl { get; init; }

    public string? Token { get; init; }
}
#pragma warning restore CA1056

/// <summary>Fetches SNS signing certificates and confirms subscriptions. Swappable in tests.</summary>
public interface ISnsGateway
{
    Task<X509Certificate2?> GetCertificateAsync(Uri url, CancellationToken cancellationToken);

    Task ConfirmSubscriptionAsync(Uri subscribeUrl, CancellationToken cancellationToken);
}

public static partial class SesWebhook
{
    /// <summary>Only certificates and confirmation links on Amazon SNS hosts are trusted.</summary>
    public static bool IsSnsUrl(Uri? url) =>
        url is { Scheme: "https" } && SnsHost().IsMatch(url.Host);

    public static string CanonicalString(SnsEnvelope e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var sb = new StringBuilder();
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                sb.Append(name).Append('\n').Append(value).Append('\n');
            }
        }

        Add("Message", e.Message);
        Add("MessageId", e.MessageId);
        if (e.Type == "Notification")
        {
            Add("Subject", e.Subject);
            Add("Timestamp", e.Timestamp);
        }
        else
        {
            Add("SubscribeURL", e.SubscribeUrl);
            Add("Timestamp", e.Timestamp);
            Add("Token", e.Token);
        }

        Add("TopicArn", e.TopicArn);
        Add("Type", e.Type);
        return sb.ToString();
    }

    public static bool VerifySignature(SnsEnvelope e, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(certificate);
        using var rsa = certificate.GetRSAPublicKey();
        var signature = new byte[512];
        if (rsa is null || !Convert.TryFromBase64String(e.Signature, signature, out var length))
        {
            return false;
        }

        var hash = e.SignatureVersion switch
        {
            "2" => HashAlgorithmName.SHA256,
#pragma warning disable CA5350 // SNS SignatureVersion 1 is defined as SHA1; we verify, we never sign with it.
            "1" => HashAlgorithmName.SHA1,
#pragma warning restore CA5350
            _ => default,
        };
        return hash != default
            && rsa.VerifyData(Encoding.UTF8.GetBytes(CanonicalString(e)), signature.AsSpan(0, length), hash, RSASignaturePadding.Pkcs1);
    }

    public static DeliveryEvent Parse(string eventId, string message)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        var type = (root.TryGetProperty("eventType", out var et) ? et : root.GetProperty("notificationType")).GetString();
        var mail = root.GetProperty("mail");
        var sesId = mail.TryGetProperty("messageId", out var id) ? id.GetString() : null;
        var internetId = mail.TryGetProperty("commonHeaders", out var h) && h.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;

        static string[] Addresses(JsonElement parent, string array) =>
            parent.TryGetProperty(array, out var list)
                ? [.. list.EnumerateArray().Select(r => r.GetProperty("emailAddress").GetString()!)]
                : [];

        switch (type)
        {
            case "Bounce":
                var bounce = root.GetProperty("bounce");
                var bounceType = bounce.GetProperty("bounceType").GetString();
                return new DeliveryEvent("ses", eventId,
                    bounceType == "Permanent" ? DeliveryEventKind.HardBounce : DeliveryEventKind.SoftBounce,
                    sesId, internetId, Addresses(bounce, "bouncedRecipients"), $"Bounce ({bounceType})");
            case "Complaint":
                return new DeliveryEvent("ses", eventId, DeliveryEventKind.Complaint, sesId, internetId,
                    Addresses(root.GetProperty("complaint"), "complainedRecipients"), "Complaint");
            case "Delivery":
                return new DeliveryEvent("ses", eventId, DeliveryEventKind.Delivered, sesId, internetId, [], "Delivery");
            case "Send":
                return new DeliveryEvent("ses", eventId, DeliveryEventKind.Sent, sesId, internetId, [], "Send");
            default:
                return new DeliveryEvent("ses", eventId, DeliveryEventKind.Ignored, sesId, internetId, [], type ?? "unknown");
        }
    }

    [GeneratedRegex(@"^sns\.[a-z0-9-]+\.amazonaws\.com(\.cn)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SnsHost();
}

internal sealed class HttpSnsGateway(IHttpClientFactory httpClients) : ISnsGateway
{
    private readonly ConcurrentDictionary<Uri, X509Certificate2> _certificates = new();

    public async Task<X509Certificate2?> GetCertificateAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!SesWebhook.IsSnsUrl(url) || !url.AbsolutePath.EndsWith(".pem", StringComparison.Ordinal))
        {
            return null;
        }

        if (_certificates.TryGetValue(url, out var cached))
        {
            return cached;
        }

        var pem = await httpClients.CreateClient(nameof(HttpSnsGateway)).GetStringAsync(url, cancellationToken);
        return _certificates.GetOrAdd(url, X509Certificate2.CreateFromPem(pem));
    }

    public async Task ConfirmSubscriptionAsync(Uri subscribeUrl, CancellationToken cancellationToken)
    {
        if (!SesWebhook.IsSnsUrl(subscribeUrl))
        {
            throw new ArgumentException("Not an Amazon SNS URL.", nameof(subscribeUrl));
        }

        using var response = await httpClients.CreateClient(nameof(HttpSnsGateway)).GetAsync(subscribeUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
