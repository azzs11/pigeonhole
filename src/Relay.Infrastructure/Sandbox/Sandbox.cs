using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure.Delivery;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Webhooks;

namespace Relay.Infrastructure.Sandbox;

/// <summary>
/// The public sandbox: faults a visitor can switch on, a test batch, simulated bounces and a live
/// check of the delivery promises. Off unless <c>Relay:Sandbox:Enabled</c> is true. It must only
/// ever run with a sink provider (Mailpit) so nothing real is sent.
/// </summary>
public sealed record SandboxOptions
{
    public const string Section = "Relay:Sandbox";

    public bool Enabled { get; init; }

    /// <summary>Mailpit's API, used to check "nothing sent twice" against what actually arrived.</summary>
    public Uri? MailpitApi { get; init; }

    /// <summary>Mailpit's web UI as the visitor's browser sees it (MailpitApi may be a container-internal address).</summary>
    public Uri? MailpitPublicUrl { get; init; }

    /// <summary>Recipients per day for self-service demo keys. Small on purpose.</summary>
    public int DemoDailyQuota { get; init; } = 200;

    /// <summary>
    /// The sandbox must never send real mail: only SMTP providers (the Mailpit sink) are allowed.
    /// Called by the worker at startup.
    /// </summary>
    public void EnsureSinkOnly(IEnumerable<Delivery.ProviderSettings> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var real = providers.Where(p => p.Type != Delivery.ProviderType.Smtp).Select(p => p.Name).ToList();
        if (Enabled && real.Count > 0)
        {
            throw new InvalidOperationException(
                $"Sandbox mode only allows SMTP sink providers (Mailpit); refusing to start with: {string.Join(", ", real)}.");
        }
    }
}

public sealed class SandboxCrashException : Exception
{
    public SandboxCrashException()
        : base("Sandbox: worker crashed after the provider accepted the message")
    {
    }

    public SandboxCrashException(string message)
        : base(message)
    {
    }

    public SandboxCrashException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Applies the sandbox switches to every provider call. Reads them from the database at most every 2 s.</summary>
internal sealed class SandboxInterceptor(IServiceScopeFactory scopes, TimeProvider time) : IProviderInterceptor
{
    private SandboxSwitches _cached = new();
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public async Task<ProviderOutcome> SendAsync(
        IEmailProvider provider, OutgoingEmail email, Func<CancellationToken, Task<ProviderOutcome>> send, CancellationToken cancellationToken)
    {
        var switches = await CurrentAsync(cancellationToken);
        if (switches.DownProviders.Contains(provider.Name, StringComparer.Ordinal))
        {
            return ProviderOutcome.Transient("sandbox: provider switched off");
        }

        if (switches.DelayMilliseconds > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(switches.DelayMilliseconds), time, cancellationToken);
        }

        var outcome = await send(cancellationToken);
        if (outcome.Kind == ProviderOutcomeKind.Sent && switches.CrashRate > 0 && RandomNumberGenerator.GetInt32(10_000) < switches.CrashRate * 10_000)
        {
            // The provider has the mail, but the worker "dies" before recording it.
            throw new SandboxCrashException();
        }

        return outcome;
    }

    private async Task<SandboxSwitches> CurrentAsync(CancellationToken cancellationToken)
    {
        if (time.GetUtcNow() - _readAt < TimeSpan.FromSeconds(2))
        {
            return _cached;
        }

        await using var scope = scopes.CreateAsyncScope();
        _cached = await scope.ServiceProvider.GetRequiredService<RelayDbContext>().SandboxSwitches.AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken) ?? new SandboxSwitches();
        _readAt = time.GetUtcNow();
        return _cached;
    }
}

public sealed record SwitchesDto(IReadOnlyList<string> DownProviders, int DelayMilliseconds, double CrashRate);

public sealed record Promise(string Name, bool Kept, string Detail);

public sealed class SandboxService(
    RelayDbContext db, SendMessageHandler sender, DeliveryEventProcessor events, SandboxOptions options,
    IHttpClientFactory http, TimeProvider time)
{
    public async Task<SwitchesDto> GetSwitchesAsync(CancellationToken cancellationToken)
    {
        var s = await db.SandboxSwitches.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? new SandboxSwitches();
        return new SwitchesDto(s.DownProviders, s.DelayMilliseconds, s.CrashRate);
    }

    public async Task<SwitchesDto> SetSwitchesAsync(SwitchesDto value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var s = await db.SandboxSwitches.SingleOrDefaultAsync(cancellationToken);
        if (s is null)
        {
            s = new SandboxSwitches();
            db.SandboxSwitches.Add(s);
        }

        s.DownProviders = [.. value.DownProviders.Distinct(StringComparer.Ordinal)];
        s.DelayMilliseconds = Math.Clamp(value.DelayMilliseconds, 0, 10_000);
        s.CrashRate = Math.Clamp(value.CrashRate, 0, 0.5);
        s.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return new SwitchesDto(s.DownProviders, s.DelayMilliseconds, s.CrashRate);
    }

    /// <summary>A throwaway tenant and key for a visitor: own data, tiny quota, all scopes.</summary>
    public async Task<IssuedKey> IssueDemoKeyAsync(ApiKeyService keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var issued = await keys.IssueAsync($"demo-{Guid.NewGuid():N}"[..17], "sandbox demo", Scopes.All, cancellationToken);
        var tenant = await db.Tenants.SingleAsync(t => t.Id == issued.Key.TenantId, cancellationToken);
        tenant.SetDailyQuota(options.DemoDailyQuota);
        await db.SaveChangesAsync(cancellationToken);
        return issued;
    }

    public Task<SendOutcome> SendBatchAsync(Guid tenantId, int count, CancellationToken cancellationToken) =>
        sender.HandleBulkAsync(tenantId, new SendBulkRequest
        {
            Recipients = [.. Enumerable.Range(1, Math.Clamp(count, 1, 500)).Select(i => $"visitor{i}@sandbox.test")],
            Subject = $"Sandbox test batch {time.GetUtcNow():HH:mm:ss}",
            Text = "A test message. It goes to the sandbox inbox (Mailpit); nothing is delivered for real.",
        }, null, cancellationToken);

    /// <summary>Plays the provider's bounce webhook for a message, through the real event processor.</summary>
    public async Task<EventResult?> BounceAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.TenantId == tenantId && m.Id == messageId, cancellationToken);
        return message is null
            ? null
            : await events.ProcessAsync(new DeliveryEvent("sandbox", Guid.NewGuid().ToString(), DeliveryEventKind.HardBounce,
                null, message.InternetMessageId, [.. message.To], "sandbox bounce (Permanent)"), cancellationToken);
    }

    /// <summary>The three promises, checked against the database and against what actually reached Mailpit.</summary>
    public async Task<IReadOnlyList<Promise>> CheckPromisesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var stale = time.GetUtcNow().AddMinutes(-5);
        var stuck = await db.Messages.CountAsync(m => m.TenantId == tenantId && m.CreatedAt < stale
            && (m.Status == MessageStatus.Accepted || m.Status == MessageStatus.Sending), cancellationToken);
        var unknown = await db.Messages.CountAsync(m => m.TenantId == tenantId && m.Status == MessageStatus.Unknown, cancellationToken);

        var duplicates = await CountMailpitDuplicatesAsync(cancellationToken);
        return
        [
            duplicates is null
                ? new Promise("Nothing sent twice", true, "Mailpit is not configured, so this cannot be checked.")
                : new Promise("Nothing sent twice", duplicates == 0,
                    duplicates == 0 ? "Every Message-ID reached the inbox once." : $"{duplicates} Message-IDs arrived more than once."),
            new Promise("Nothing lost", stuck == 0,
                stuck == 0 ? "No message has been waiting more than 5 minutes." : $"{stuck} messages have waited more than 5 minutes."),
            new Promise("Every message explained", true,
                unknown == 0 ? "All messages are in a known state." : $"{unknown} in Unknown, each with the reason in its timeline."),
        ];
    }

    private async Task<int?> CountMailpitDuplicatesAsync(CancellationToken cancellationToken)
    {
        if (options.MailpitApi is null)
        {
            return null;
        }

        try
        {
            var page = await http.CreateClient(nameof(SandboxService))
                .GetFromJsonAsync<MailpitPage>(new Uri(options.MailpitApi, "api/v1/messages?limit=1000"), cancellationToken);
            return page?.Messages.GroupBy(m => m.MessageId, StringComparer.Ordinal).Count(g => g.Count() > 1) ?? 0;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private sealed record MailpitPage([property: JsonPropertyName("messages")] IReadOnlyList<MailpitMessage> Messages);

    private sealed record MailpitMessage([property: JsonPropertyName("MessageID")] string MessageId);
}

public sealed record TenantStats(
    IReadOnlyDictionary<string, int> ByStatus, int OutboxDepth, int AcceptedLast5Min, int SentLast5Min,
    int QuotaUsed, int DailyQuota, string? PausedReason);

public sealed class OpsQueries(RelayDbContext db, TimeProvider time)
{
    public async Task<TenantStats> StatsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var since = now.AddMinutes(-5);
        var byStatus = await db.Messages.Where(m => m.TenantId == tenantId)
            .GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key.ToString(), x => x.Count, cancellationToken);
        var depth = await db.Outbox.CountAsync(o => o.TenantId == tenantId && o.ProcessedAt == null, cancellationToken);
        var accepted = await db.Messages.CountAsync(m => m.TenantId == tenantId && m.CreatedAt >= since, cancellationToken);
        var sent = await db.Messages.CountAsync(m => m.TenantId == tenantId && m.UpdatedAt >= since
            && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered), cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, cancellationToken);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return new TenantStats(byStatus, depth, accepted, sent,
            tenant.QuotaDay == today ? tenant.QuotaUsed : 0, tenant.DailyQuota, tenant.PauseReason);
    }

    public async Task<IReadOnlyList<ProviderHealth>> ProvidersAsync(CancellationToken cancellationToken) =>
        await db.ProviderHealth.AsNoTracking().OrderBy(p => p.Position).ToListAsync(cancellationToken);

    /// <summary>Upserts the worker's live provider snapshot.</summary>
    public async Task SaveProviderHealthAsync(IEnumerable<ProviderSnapshot> snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = time.GetUtcNow();
        foreach (var p in snapshot)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO provider_health (name, position, circuit_state, attempts, failures, last_error, updated_at)
                VALUES ({p.Name}, {p.Position}, {p.CircuitState}, {p.Attempts}, {p.Failures}, {p.LastError}, {now})
                ON CONFLICT (name) DO UPDATE SET position = EXCLUDED.position, circuit_state = EXCLUDED.circuit_state,
                    attempts = EXCLUDED.attempts, failures = EXCLUDED.failures, last_error = EXCLUDED.last_error,
                    updated_at = EXCLUDED.updated_at
                """, cancellationToken);
        }
    }
}
