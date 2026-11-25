using System.Globalization;
using System.Text.Json;
using Relay.Infrastructure.Webhooks;

namespace Relay.Api.Endpoints;

/// <summary>
/// Provider callbacks. Anonymous, but every request must carry a valid provider signature.
/// Non-2xx makes the provider redeliver, so we only return 2xx once the event is stored.
/// </summary>
internal static class WebhookEndpoints
{
    private const int MaxBodyBytes = 256 * 1024;

    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/webhooks").WithTags("Webhooks").AllowAnonymous();
        group.MapPost("/resend", Resend);
        group.MapPost("/ses", Ses);
    }

    private static async Task<IResult> Resend(
        HttpRequest request, WebhookOptions options, DeliveryEventProcessor processor, TimeProvider time, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(options.Resend.SigningSecret))
        {
            return NotConfigured();
        }

        var body = await ReadBodyAsync(request, cancellationToken);
        if (body is null)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "Payload too large.");
        }

        string? id = request.Headers["svix-id"];
        if (!ResendWebhook.Verify(options.Resend.SigningSecret, id, request.Headers["svix-timestamp"],
                request.Headers["svix-signature"], body, time.GetUtcNow(), TimeSpan.FromSeconds(options.Resend.ToleranceSeconds)))
        {
            return Problem(StatusCodes.Status401Unauthorized, "Invalid or expired signature.");
        }

        DeliveryEvent e;
        try
        {
            e = ResendWebhook.Parse(id!, body);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Problem(StatusCodes.Status400BadRequest, "Unrecognised event payload.");
        }

        return Result(await processor.ProcessAsync(e, cancellationToken));
    }

    private static async Task<IResult> Ses(
        HttpRequest request, WebhookOptions options, ISnsGateway sns, DeliveryEventProcessor processor,
        TimeProvider time, CancellationToken cancellationToken)
    {
        if (options.Ses.AllowedTopicArns.Count == 0)
        {
            return NotConfigured();
        }

        var body = await ReadBodyAsync(request, cancellationToken);
        if (body is null)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "Payload too large.");
        }

        SnsEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SnsEnvelope>(body);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null || !options.Ses.AllowedTopicArns.Contains(envelope.TopicArn, StringComparer.Ordinal))
        {
            return Problem(StatusCodes.Status403Forbidden, "Unknown topic.");
        }

        var fresh = DateTimeOffset.TryParse(envelope.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sentAt)
            && (time.GetUtcNow() - sentAt).Duration() <= TimeSpan.FromSeconds(options.Ses.ToleranceSeconds);
        var certificate = Uri.TryCreate(envelope.SigningCertUrl, UriKind.Absolute, out var certUrl)
            ? await sns.GetCertificateAsync(certUrl, cancellationToken)
            : null;
        if (!fresh || certificate is null || !SesWebhook.VerifySignature(envelope, certificate))
        {
            return Problem(StatusCodes.Status401Unauthorized, "Invalid or expired signature.");
        }

        switch (envelope.Type)
        {
            case "SubscriptionConfirmation" when Uri.TryCreate(envelope.SubscribeUrl, UriKind.Absolute, out var subscribe):
                await sns.ConfirmSubscriptionAsync(subscribe, cancellationToken);
                return TypedResults.Ok(new { result = "SubscriptionConfirmed" });
            case "Notification":
                DeliveryEvent e;
                try
                {
                    e = SesWebhook.Parse(envelope.MessageId, envelope.Message);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    return Problem(StatusCodes.Status400BadRequest, "Unrecognised event payload.");
                }

                return Result(await processor.ProcessAsync(e, cancellationToken));
            default:
                return TypedResults.Ok(new { result = "Ignored" });
        }
    }

    private static IResult Result(EventResult result) => result == EventResult.RetryLater
        ? Problem(StatusCodes.Status503ServiceUnavailable, "Message is mid-send; retry later.")
        : TypedResults.Ok(new { result = result.ToString() });

    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var reader = new StreamReader(request.Body);
        var buffer = new char[MaxBodyBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return read > MaxBodyBytes ? null : new string(buffer, 0, read);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult NotConfigured() => Problem(StatusCodes.Status404NotFound, "Webhook endpoint is not configured.");

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Problem(int status, string title) => TypedResults.Problem(statusCode: status, title: title);
}
