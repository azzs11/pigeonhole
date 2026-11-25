using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Relay.Infrastructure.Webhooks;

namespace Relay.IntegrationTests;

/// <summary>Test signing material for Resend (Svix HMAC) and SES (SNS RSA). Never real secrets.</summary>
public static class TestSigning
{
    /// <summary>Random per test run, so no key-shaped literal ever lives in the repo.</summary>
    public static string ResendSecret { get; } = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    public const string TopicArn = "arn:aws:sns:us-east-1:000000000000:relay-test";

    private static readonly RSA Key = RSA.Create(2048);
    private static readonly X509Certificate2 Certificate =
        new CertificateRequest("CN=sns.test", Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

    public static FakeSnsGateway Sns { get; } = new(Certificate);

    public static HttpRequestMessage ResendRequest(object payload, string? eventId = null, DateTimeOffset? at = null, bool tamper = false)
    {
        var id = eventId ?? $"msg_{Guid.NewGuid():N}";
        var body = JsonSerializer.Serialize(payload);
        var ts = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/resend")
        {
            Content = new StringContent(tamper ? body + " " : body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("svix-id", id);
        request.Headers.Add("svix-timestamp", ts);
        request.Headers.Add("svix-signature", ResendWebhook.Sign(ResendSecret, id, ts, body));
        return request;
    }

    public static HttpRequestMessage SesRequest(object? sesEvent, string type = "Notification", string? topic = null, bool tamper = false)
    {
        var envelope = new SnsEnvelope
        {
            Type = type,
            MessageId = Guid.NewGuid().ToString(),
            TopicArn = topic ?? TopicArn,
            Message = sesEvent is null ? "confirm" : JsonSerializer.Serialize(sesEvent),
            Timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            SignatureVersion = "2",
            SigningCertUrl = "https://sns.us-east-1.amazonaws.com/SimpleNotificationService-test.pem",
            SubscribeUrl = type == "SubscriptionConfirmation" ? "https://sns.us-east-1.amazonaws.com/?Action=ConfirmSubscription&Token=t" : null,
            Token = type == "SubscriptionConfirmation" ? "t" : null,
        };
        var signature = Key.SignData(Encoding.UTF8.GetBytes(SesWebhook.CanonicalString(envelope)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        envelope = envelope with { Signature = Convert.ToBase64String(signature) };
        if (tamper)
        {
            envelope = envelope with { Message = envelope.Message + " " };
        }

        return new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/ses")
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), new MediaTypeHeaderValue("text/plain")),
        };
    }
}

public sealed class FakeSnsGateway(X509Certificate2 certificate) : ISnsGateway
{
    public List<Uri> Confirmed { get; } = [];

    public Task<X509Certificate2?> GetCertificateAsync(Uri url, CancellationToken cancellationToken) =>
        Task.FromResult<X509Certificate2?>(SesWebhook.IsSnsUrl(url) ? certificate : null);

    public Task ConfirmSubscriptionAsync(Uri subscribeUrl, CancellationToken cancellationToken)
    {
        lock (Confirmed)
        {
            Confirmed.Add(subscribeUrl);
        }

        return Task.CompletedTask;
    }
}
