using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Relay.Application;
using Relay.Infrastructure;
using Relay.Infrastructure.Persistence;

const string Usage = """
    Usage:
      relay db migrate
      relay keys create --tenant <name> [--name <key name>] [--scopes messages:send,messages:read]
      relay keys revoke --prefix <rly_xxxxxxxx>
      relay tenants quota --tenant <name> --daily <recipients per UTC day>
      relay tenants pause --tenant <name> --reason <text>
      relay tenants resume --tenant <name>
      relay runs create --tenant <name> --trusted a@x.test,b@y.test [--allowed-domains example.com] [--ttl 120]

    Connection string: ConnectionStrings__Relay environment variable, else appsettings.json.
    """;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
var connectionString = builder.Configuration.GetConnectionString("Relay")
    ?? throw new InvalidOperationException("ConnectionStrings:Relay is not configured.");
builder.Services.AddRelayCore(connectionString, new MessagingOptions());
using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();
var services = scope.ServiceProvider;

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

switch (args)
{
    case ["db", "migrate", ..]:
        await services.GetRequiredService<RelayDbContext>().Database.MigrateAsync();
        Console.WriteLine("Database is up to date.");
        return 0;

    case ["keys", "create", ..] when Option("--tenant") is { } tenant:
        var scopes = Option("--scopes")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [.. Scopes.All];
        var issued = await services.GetRequiredService<ApiKeyService>()
            .IssueAsync(tenant, Option("--name") ?? "default", scopes, CancellationToken.None);
        Console.WriteLine($"Tenant:  {tenant}");
        Console.WriteLine($"Prefix:  {issued.Key.Prefix}");
        Console.WriteLine($"Scopes:  {string.Join(", ", issued.Key.Scopes)}");
        Console.WriteLine($"API key: {issued.Plaintext}");
        Console.WriteLine("Store this key now. It is not stored and cannot be shown again.");
        return 0;

    case ["keys", "revoke", ..] when Option("--prefix") is { } prefix:
        var revoked = await services.GetRequiredService<ApiKeyService>().RevokeAsync(prefix, CancellationToken.None);
        Console.WriteLine(revoked ? $"Revoked {prefix}." : $"No key with prefix {prefix}.");
        return revoked ? 0 : 1;

    case ["runs", "create", ..] when Option("--tenant") is { } runTenant:
        var runDb = services.GetRequiredService<RelayDbContext>();
        var owner = await runDb.Tenants.SingleOrDefaultAsync(t => t.Name == runTenant);
        if (owner is null)
        {
            await Console.Error.WriteLineAsync($"No tenant named {runTenant}. Create one with `relay keys create --tenant {runTenant}`.");
            return 1;
        }

        string[] Csv(string name) => Option(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var (created, runErrors) = await services.GetRequiredService<Relay.Guard.AgentGuardService>().CreateRunAsync(owner.Id,
            new Relay.Guard.CreateRunRequest
            {
                Name = Option("--name") ?? "cli run",
                TrustedRecipients = Csv("--trusted"),
                Policy = new Relay.Guard.GuardPolicy { AllowedDomains = Csv("--allowed-domains") },
                TtlMinutes = int.TryParse(Option("--ttl"), out var ttl) ? ttl : 120,
            }, CancellationToken.None);
        if (created is null)
        {
            await Console.Error.WriteLineAsync(string.Join("; ", runErrors!.SelectMany(e => e.Value)));
            return 1;
        }

        var mcpProject = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Relay.Mcp"));
        Console.WriteLine($"Run:      {created.Run.Id} (expires {created.Run.ExpiresAt:u})");
        Console.WriteLine($"Token:    {created.Token}");
        Console.WriteLine("Shown once. Add the MCP server to Claude Code with:");
        Console.WriteLine();
        Console.WriteLine($"  claude mcp add relay --env RELAY_URL=http://localhost:5238/ --env RELAY_RUN_TOKEN={created.Token} -- dotnet run --project {mcpProject}");
        return 0;

    case ["tenants", var action, ..] when Option("--tenant") is { } name && action is "quota" or "pause" or "resume":
        var db = services.GetRequiredService<RelayDbContext>();
        var found = await db.Tenants.SingleOrDefaultAsync(t => t.Name == name);
        if (found is null)
        {
            await Console.Error.WriteLineAsync($"No tenant named {name}.");
            return 1;
        }

        switch (action)
        {
            case "quota" when int.TryParse(Option("--daily"), out var daily):
                found.SetDailyQuota(daily);
                break;
            case "pause":
                found.PauseSending(Option("--reason") ?? "Paused by an operator.", DateTimeOffset.UtcNow);
                break;
            case "resume":
                found.ResumeSending();
                break;
            default:
                await Console.Error.WriteLineAsync(Usage);
                return 2;
        }

        await db.SaveChangesAsync();
        Console.WriteLine($"{name}: quota {found.DailyQuota}/day, {(found.IsSendingPaused ? $"paused ({found.PauseReason})" : "sending")}.");
        return 0;

    default:
        await Console.Error.WriteLineAsync(Usage);
        return 2;
}
