using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Relay.Application;

namespace Relay.Infrastructure.Delivery;

/// <summary>
/// Resend HTTP API. Sends an Idempotency-Key (the message id), so a retry after an
/// unknown outcome is deduplicated by Resend.
/// </summary>
internal sealed class ResendEmailProvider(string name, ResendSettings settings, IHttpClientFactory httpClients) : IEmailProvider
{
    public string Name => name;
    public bool SupportsIdempotentRetry => true;

    public async Task<ProviderOutcome> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUrl, "emails"))
        {
            Content = JsonContent.Create(new ResendEmail(
                email.From, email.To, Empty(email.Cc), Empty(email.Bcc), email.Subject, email.HtmlBody, email.TextBody,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Message-ID"] = email.InternetMessageId })),
        };
        request.Headers.Authorization = new("Bearer", settings.ApiKey);
        request.Headers.Add("Idempotency-Key", email.MessageId.ToString());

        var http = httpClients.CreateClient(nameof(ResendEmailProvider));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            return ProviderOutcome.Transient($"Resend unreachable ({ex.HttpRequestError})");
        }
        catch (HttpRequestException ex)
        {
            return ProviderOutcome.Unknown($"Resend connection lost ({ex.HttpRequestError})");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<ResendResponse>(cancellationToken);
                return ProviderOutcome.Sent(body?.Id);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => ProviderOutcome.Transient("Resend 429: rate limited"),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderOutcome.Transient($"Resend {status}: credentials refused"),
                HttpStatusCode.Conflict => ProviderOutcome.Unknown("Resend 409: same idempotency key still in progress"),
                _ when status >= 500 => ProviderOutcome.Unknown($"Resend {status}: server error"),
                _ => ProviderOutcome.Rejected($"Resend {status}: request rejected"),
            };
        }
    }

    private static IReadOnlyList<string>? Empty(IReadOnlyList<string> list) => list.Count == 0 ? null : list;

    private sealed record ResendEmail(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] IReadOnlyList<string> To,
        [property: JsonPropertyName("cc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Cc,
        [property: JsonPropertyName("bcc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Bcc,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Html,
        [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
        [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string> Headers);

    private sealed record ResendResponse([property: JsonPropertyName("id")] string? Id);
}
