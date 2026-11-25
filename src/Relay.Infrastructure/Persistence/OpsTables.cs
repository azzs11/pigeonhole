namespace Relay.Infrastructure.Persistence;

/// <summary>Per-provider health, written by workers every 15 s from the live circuit breakers.</summary>
public sealed class ProviderHealth
{
    public string Name { get; set; } = string.Empty;
    public int Position { get; set; }
    public string CircuitState { get; set; } = "Closed";
    public int Attempts { get; set; }
    public int Failures { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Sandbox-only fault switches (single row). Workers apply them to every provider call.</summary>
public sealed class SandboxSwitches
{
    public int Id { get; set; } = 1;
    public IReadOnlyList<string> DownProviders { get; set; } = [];
    public int DelayMilliseconds { get; set; }
    public double CrashRate { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
