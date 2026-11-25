using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Relay.Infrastructure.Webhooks;
using Shouldly;

namespace Relay.UnitTests;

public class ResendWebhookTests
{
    // Random per run: no key-shaped literal in the repo.
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Ts = Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
    private const string Body = """{"type":"email.delivered","data":{"email_id":"re_1"}}""";

    private static bool Verify(string? signature, string body = Body, string? ts = null, string id = "msg_1") =>
        ResendWebhook.Verify(Secret, id, ts ?? Ts, signature, body, Now, TimeSpan.FromMinutes(5));

    [Fact]
    public void Valid_signature_verifies() => Verify(ResendWebhook.Sign(Secret, "msg_1", Ts, Body)).ShouldBeTrue();

    [Fact]
    public void Any_of_several_signatures_may_match() =>
        Verify("v1,AAAA " + ResendWebhook.Sign(Secret, "msg_1", Ts, Body)).ShouldBeTrue();

    [Fact]
    public void Tampered_body_fails() =>
        Verify(ResendWebhook.Sign(Secret, "msg_1", Ts, Body), Body.Replace("re_1", "re_2", StringComparison.Ordinal)).ShouldBeFalse();

    [Fact]
    public void Different_event_id_fails() =>
        Verify(ResendWebhook.Sign(Secret, "msg_1", Ts, Body), id: "msg_2").ShouldBeFalse();

    [Fact]
    public void Stale_timestamp_fails_even_with_valid_signature()
    {
        var old = Now.AddMinutes(-10).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Verify(ResendWebhook.Sign(Secret, "msg_1", old, Body), ts: old).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1,not-base64!")]
    [InlineData("v2,abc")]
    public void Malformed_signatures_fail(string? signature) => Verify(signature).ShouldBeFalse();

    [Theory]
    [InlineData("""{"type":"email.delivered","data":{"email_id":"re_1","to":["a@x.test"]}}""", DeliveryEventKind.Delivered)]
    [InlineData("""{"type":"email.bounced","data":{"email_id":"re_1","bounce":{"type":"Permanent"}}}""", DeliveryEventKind.HardBounce)]
    [InlineData("""{"type":"email.bounced","data":{"email_id":"re_1","bounce":{"type":"Transient"}}}""", DeliveryEventKind.SoftBounce)]
    [InlineData("""{"type":"email.complained","data":{"email_id":"re_1"}}""", DeliveryEventKind.Complaint)]
    [InlineData("""{"type":"email.opened","data":{"email_id":"re_1"}}""", DeliveryEventKind.Ignored)]
    public void Events_are_classified(string body, DeliveryEventKind expected)
    {
        var e = ResendWebhook.Parse("msg_1", body);

        e.Kind.ShouldBe(expected);
        e.ProviderMessageId.ShouldBe("re_1");
        e.EventId.ShouldBe("msg_1");
    }
}

public sealed class SesWebhookTests : IDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    private readonly X509Certificate2 _cert;

    public SesWebhookTests() =>
        _cert = new CertificateRequest("CN=sns.test", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

    public void Dispose()
    {
        _cert.Dispose();
        _key.Dispose();
    }

    private SnsEnvelope Signed(SnsEnvelope e, string version = "2")
    {
        var unsigned = e with { SignatureVersion = version };
        var hash = version == "2" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1;
        var signature = _key.SignData(Encoding.UTF8.GetBytes(SesWebhook.CanonicalString(unsigned)), hash, RSASignaturePadding.Pkcs1);
        return unsigned with { Signature = Convert.ToBase64String(signature) };
    }

    private static SnsEnvelope Notification(string message = "{}") => new()
    {
        Type = "Notification",
        MessageId = "sns-1",
        TopicArn = "arn:aws:sns:us-east-1:123:ses",
        Message = message,
        Timestamp = "2026-11-25T12:00:00.000Z",
    };

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public void Valid_signature_verifies(string version) =>
        SesWebhook.VerifySignature(Signed(Notification(), version), _cert).ShouldBeTrue();

    [Fact]
    public void Tampered_message_fails() =>
        SesWebhook.VerifySignature(Signed(Notification()) with { Message = "{\"x\":1}" }, _cert).ShouldBeFalse();

    [Fact]
    public void Unknown_signature_version_fails() =>
        SesWebhook.VerifySignature(Signed(Notification()) with { SignatureVersion = "3" }, _cert).ShouldBeFalse();

    [Theory]
    [InlineData("https://sns.us-east-1.amazonaws.com/SimpleNotificationService-abc.pem", true)]
    [InlineData("https://sns.cn-north-1.amazonaws.com.cn/cert.pem", true)]
    [InlineData("http://sns.us-east-1.amazonaws.com/cert.pem", false)]
    [InlineData("https://sns.us-east-1.amazonaws.com.evil.test/cert.pem", false)]
    [InlineData("https://evil.test/sns.us-east-1.amazonaws.com/cert.pem", false)]
    public void Only_amazon_sns_hosts_are_trusted(string url, bool trusted) =>
        SesWebhook.IsSnsUrl(new Uri(url)).ShouldBe(trusted);

    [Fact]
    public void Permanent_bounce_maps_recipients_and_both_ids()
    {
        var e = SesWebhook.Parse("sns-1", """
            {"notificationType":"Bounce","mail":{"messageId":"ses-1","commonHeaders":{"messageId":"<abc@relay.test>"}},
             "bounce":{"bounceType":"Permanent","bouncedRecipients":[{"emailAddress":"gone@example.test"}]}}
            """);

        e.Kind.ShouldBe(DeliveryEventKind.HardBounce);
        e.ProviderMessageId.ShouldBe("ses-1");
        e.InternetMessageId.ShouldBe("<abc@relay.test>");
        e.Recipients.ShouldBe(["gone@example.test"]);
    }

    [Theory]
    [InlineData("""{"eventType":"Bounce","mail":{"messageId":"s"},"bounce":{"bounceType":"Transient","bouncedRecipients":[]}}""", DeliveryEventKind.SoftBounce)]
    [InlineData("""{"eventType":"Complaint","mail":{"messageId":"s"},"complaint":{"complainedRecipients":[{"emailAddress":"a@x.test"}]}}""", DeliveryEventKind.Complaint)]
    [InlineData("""{"eventType":"Delivery","mail":{"messageId":"s"}}""", DeliveryEventKind.Delivered)]
    [InlineData("""{"eventType":"Open","mail":{"messageId":"s"}}""", DeliveryEventKind.Ignored)]
    public void Event_types_are_classified(string message, DeliveryEventKind expected) =>
        SesWebhook.Parse("sns-1", message).Kind.ShouldBe(expected);
}
