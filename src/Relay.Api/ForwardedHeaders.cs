using Microsoft.AspNetCore.HttpOverrides;

namespace Relay.Api;

/// <summary>
/// Behind a reverse proxy (the dashboard's nginx), the client IP arrives in X-Forwarded-For.
/// It is trusted only from the networks listed in <c>Relay:Proxy:KnownNetworks</c>, so a client
/// talking to the API directly cannot pick its own IP and dodge the per-IP limits. Off when empty.
/// </summary>
internal static class ForwardedHeaders
{
    public const string Section = "Relay:Proxy:KnownNetworks";

    public static IApplicationBuilder UseRelayForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        var networks = configuration.GetSection(Section).Get<string[]>() ?? [];
        if (networks.Length == 0)
        {
            return app;
        }

        var options = new ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in networks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }

        return app.UseForwardedHeaders(options);
    }
}
