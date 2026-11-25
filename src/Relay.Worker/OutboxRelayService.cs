using Relay.Application;
using Relay.Infrastructure.Delivery;

namespace Relay.Worker;

/// <summary>Polls the outbox. Runs as many instances as you like; claiming is SKIP LOCKED.</summary>
internal sealed partial class OutboxRelayService(
    DeliveryEngine engine, DeliveryOptions options, TimeProvider time, ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, engine.WorkerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed;
            try
            {
                claimed = await engine.RunOnceAsync(stoppingToken);
            }
            catch (Relay.Infrastructure.Sandbox.SandboxCrashException)
            {
                // Sandbox fault: behave like a process that died mid-batch. Leases expire and the reclaim marks Unknown.
                LogSandboxCrash(logger);
                claimed = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A database blip must not kill the worker; leases make the retry safe.
                LogBatchFailed(logger, ex);
                claimed = 0;
            }

            if (claimed == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds), time, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox relay {WorkerId} started")]
    private static partial void LogStarted(ILogger logger, string workerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sandbox crash injected after a provider accepted a message")]
    private static partial void LogSandboxCrash(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox batch failed; retrying after the poll interval")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);
}
