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
