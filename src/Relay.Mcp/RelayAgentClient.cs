using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Relay.Mcp;

/// <summary>Calls the Relay API with a run token. Every check happens server-side; this client only forwards.</summary>
internal sealed class RelayAgentClient(HttpClient http)
{
    public static HttpClient CreateHttpClient(Uri baseUrl, string runToken)
    {
        var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
        Authorize(http, runToken);
        return http;
    }

    public static void Authorize(HttpClient http, string runToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runToken);
    }

    public Task<string> SendAsync(object message, bool dryRun, CancellationToken cancellationToken) =>
        ReadAsync(http.PostAsJsonAsync(new Uri($"v1/agent/messages?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative), message, cancellationToken), cancellationToken);

    public Task<string> ListSentAsync(CancellationToken cancellationToken) =>
        ReadAsync(http.GetAsync(new Uri("v1/agent/messages", UriKind.Relative), cancellationToken), cancellationToken);

    public Task<string> StatusAsync(Guid messageId, CancellationToken cancellationToken) =>
        ReadAsync(http.GetAsync(new Uri($"v1/agent/messages/{messageId}", UriKind.Relative), cancellationToken), cancellationToken);

    public Task<string> RequestApprovalAsync(object message, string reason, CancellationToken cancellationToken) =>
        ReadAsync(http.PostAsJsonAsync(new Uri("v1/agent/approvals", UriKind.Relative), new { message, reason }, cancellationToken), cancellationToken);

    /// <summary>Returns the body for both success and refusal: the agent needs the Guard's reasons either way.</summary>
    private static async Task<string> ReadAsync(Task<HttpResponseMessage> call, CancellationToken cancellationToken)
    {
        using var response = await call;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return $"HTTP {(int)response.StatusCode}\n{body}";
    }
}
