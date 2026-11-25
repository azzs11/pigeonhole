using Microsoft.EntityFrameworkCore;
using Relay.Infrastructure.Delivery;
using Relay.Infrastructure.Observability;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Sandbox;

namespace Relay.Worker;

/// <summary>Samples messages-by-state and outbox depth every 5 s for the gauges and the dashboard.</summary>
internal sealed partial class MetricsSnapshotService(
    IServiceScopeFactory scopes, QueueMetrics metrics, ProviderChain providers, TimeProvider time, ILogger<MetricsSnapshotService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
                var byStatus = await db.Messages.GroupBy(m => m.Status)
                    .Select(g => new { Status = g.Key, Count = g.LongCount() })
                    .ToDictionaryAsync(x => x.Status.ToString(), x => x.Count, stoppingToken);
                var depth = await db.Outbox.LongCountAsync(o => o.ProcessedAt == null, stoppingToken);
                metrics.Update(byStatus, depth);
                await scope.ServiceProvider.GetRequiredService<OpsQueries>().SaveProviderHealthAsync(providers.Snapshot(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSnapshotFailed(logger, ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metrics snapshot failed")]
    private static partial void LogSnapshotFailed(ILogger logger, Exception exception);
}
