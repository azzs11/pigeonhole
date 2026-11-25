using System.Text.Json;
using Relay.Application;
using Relay.Infrastructure.Delivery;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Relay.IntegrationTests;

/// <summary>Provider adapters against recorded-style HTTP responses. No real provider is called.</summary>
public sealed class ProviderContractTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    private static readonly OutgoingEmail Email = new(
        Guid.Parse("0193b1c2-0000-7000-8000-000000000001"), "<0193b1c2000070008000000000000001@relay.test>",
        "from@relay.test", ["to@example.test"], [], ["hidden@example.test"], "Subject", "Text", "<p>Html</p>");

    public void Dispose()
    {
        _ses?.Dispose();
        _server.Dispose();
    }

    private ResendEmailProvider Resend() => new(
        "resend", new ResendSettings { ApiKey = "re_test", BaseUrl = new Uri(_server.Url! + "/") }, new SimpleHttpClientFactory());

    private void Respond(string path, int status, object? body = null, string? errorType = null)
    {
        var response = Response.Create().WithStatusCode(status);
        if (body is not null)
        {
            response = response.WithBodyAsJson(body);
        }

        if (errorType is not null)
        {
            response = response.WithHeader("x-amzn-ErrorType", errorType);
        }

        _server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(response);
    }

    [Fact]
    public async Task Resend_success_sends_idempotency_key_and_message_id()
    {
        Respond("/emails", 200, new { id = "re_123" });

        var outcome = await Resend().SendAsync(Email, default);

        outcome.ShouldBe(ProviderOutcome.Sent("re_123"));
        var request = _server.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Headers!["Idempotency-Key"].ShouldBe([Email.MessageId.ToString()]);
        request.Headers["Authorization"].ShouldBe(["Bearer re_test"]);
        using var json = JsonDocument.Parse(request.Body!);
        json.RootElement.GetProperty("headers").GetProperty("Message-ID").GetString().ShouldBe(Email.InternetMessageId);
        json.RootElement.GetProperty("bcc")[0].GetString().ShouldBe("hidden@example.test");
        json.RootElement.TryGetProperty("cc", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(422, ProviderOutcomeKind.Rejected)]
    [InlineData(400, ProviderOutcomeKind.Rejected)]
    [InlineData(429, ProviderOutcomeKind.Transient)]
    [InlineData(401, ProviderOutcomeKind.Transient)]
    [InlineData(503, ProviderOutcomeKind.Unknown)]
    [InlineData(409, ProviderOutcomeKind.Unknown)]
    public async Task Resend_errors_are_classified(int status, ProviderOutcomeKind expected)
    {
        Respond("/emails", status, new { message = "error" });

        (await Resend().SendAsync(Email, default)).Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task Resend_unreachable_is_transient()
    {
        var url = _server.Url!;
        _server.Stop();

        var provider = new ResendEmailProvider(
            "resend", new ResendSettings { ApiKey = "k", BaseUrl = new Uri(url + "/") }, new SimpleHttpClientFactory());

        (await provider.SendAsync(Email, default)).Kind.ShouldBe(ProviderOutcomeKind.Transient);
    }

    private Amazon.SimpleEmailV2.IAmazonSimpleEmailServiceV2? _ses;

    private SesEmailProvider Ses()
    {
        _ses ??= SesEmailProvider.CreateClient(new SesSettings
        {
            Region = "us-east-1",
            ServiceUrl = new Uri(_server.Url!),
            AccessKeyId = "test",
            SecretAccessKey = "test",
        });
        return new SesEmailProvider("ses", _ses, TimeProvider.System);
    }

    [Fact]
    public async Task Ses_success_returns_provider_message_id_and_sends_raw_mime_without_bcc_header()
    {
        Respond("/v2/email/outbound-emails", 200, new { MessageId = "ses-1" });

        (await Ses().SendAsync(Email, default)).ShouldBe(ProviderOutcome.Sent("ses-1"));

        using var json = JsonDocument.Parse(_server.LogEntries.ShouldHaveSingleItem().RequestMessage!.Body!);
        var mime = System.Text.Encoding.UTF8.GetString(
            json.RootElement.GetProperty("Content").GetProperty("Raw").GetProperty("Data").GetBytesFromBase64());
        mime.ShouldContain($"Message-Id: {Email.InternetMessageId}", Case.Insensitive);
        mime.ShouldNotContain("hidden@example.test");
        json.RootElement.GetProperty("Destination").GetProperty("BccAddresses")[0].GetString().ShouldBe("hidden@example.test");
    }

    [Theory]
    [InlineData(400, "MessageRejected", ProviderOutcomeKind.Rejected)]
    [InlineData(429, "TooManyRequestsException", ProviderOutcomeKind.Transient)]
    [InlineData(500, "InternalFailure", ProviderOutcomeKind.Unknown)]
    public async Task Ses_errors_are_classified(int status, string errorType, ProviderOutcomeKind expected)
    {
        Respond("/v2/email/outbound-emails", status, new { message = "error" }, errorType);

        (await Ses().SendAsync(Email, default)).Kind.ShouldBe(expected);
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
