var builder = Host.CreateApplicationBuilder(args);

// The outbox relay hosted service is registered here in Phase 2.

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
