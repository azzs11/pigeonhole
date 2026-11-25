using Microsoft.EntityFrameworkCore;
using Relay.Infrastructure.Observability;
using Relay.Infrastructure.Persistence;

namespace Relay.Worker;

/// <summary>Samples messages-by-state and outbox depth every 15 s for the gauges.</summary>
internal sealed partial class MetricsSnapshotService(
    IServiceScopeFactory scopes, QueueMetrics metrics, TimeProvider time, ILogger<MetricsSnapshotService> logger) : BackgroundService
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSnapshotFailed(logger, ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(15), time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metrics snapshot failed")]
    private static partial void LogSnapshotFailed(ILogger logger, Exception exception);
}
