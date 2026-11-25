using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Relay.Api;

internal sealed record RateLimitOptions
{
    public const string Section = "Relay:RateLimit";

    /// <summary>Per API key: sustained requests per second and burst size.</summary>
    public int PermitsPerSecond { get; init; } = 50;
    public int Burst { get; init; } = 100;

    /// <summary>Per client IP for unauthenticated requests (webhooks, bad keys).</summary>
    public int AnonymousPermitsPerSecond { get; init; } = 20;
    public int AnonymousBurst { get; init; } = 40;

    /// <summary>Self-service sandbox demo keys per client IP per hour.</summary>
    public int DemoKeysPerHour { get; init; } = 5;
}

internal static class RateLimiting
{
    public const string DemoKeyPolicy = "demo-key";

    public static IServiceCollection AddRelayRateLimiting(this IServiceCollection services, RateLimitOptions options) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(DemoKeyPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                $"demo:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.DemoKeysPerHour, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var key = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                return key is null
                    ? Bucket($"ip:{context.Connection.RemoteIpAddress}", options.AnonymousPermitsPerSecond, options.AnonymousBurst)
                    : Bucket($"key:{key}", options.PermitsPerSecond, options.Burst);
            });
            o.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new()
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = { Status = StatusCodes.Status429TooManyRequests, Title = "Too many requests." },
                });
            };
        });

    private static RateLimitPartition<string> Bucket(string partition, int perSecond, int burst) =>
        RateLimitPartition.GetTokenBucketLimiter(partition, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            TokensPerPeriod = perSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
}
