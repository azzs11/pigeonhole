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

    default:
        await Console.Error.WriteLineAsync(Usage);
        return 2;
}
