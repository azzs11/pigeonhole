namespace Relay.Domain;

public sealed class Tenant
{
    private Tenant()
    {
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Recipients this tenant may send to per UTC day. Consumed atomically in SQL.</summary>
    public int DailyQuota { get; private set; }
    public int QuotaUsed { get; private set; }
    public DateOnly? QuotaDay { get; private set; }

    /// <summary>Set by the reputation guard (or an operator); blocks new sends until resumed.</summary>
    public DateTimeOffset? SendingPausedAt { get; private set; }
    public string? PauseReason { get; private set; }

    public bool IsSendingPaused => SendingPausedAt is not null;

    public static Tenant Create(Guid id, string name, int dailyQuota, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(dailyQuota);
        return new Tenant { Id = id, Name = name.Trim(), IsActive = true, DailyQuota = dailyQuota, CreatedAt = now };
    }

    public void Deactivate() => IsActive = false;

    public void SetDailyQuota(int dailyQuota)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dailyQuota);
        DailyQuota = dailyQuota;
    }

    public void PauseSending(string reason, DateTimeOffset at)
    {
        if (SendingPausedAt is null)
        {
            SendingPausedAt = at;
            PauseReason = reason;
        }
    }

    public void ResumeSending()
    {
        SendingPausedAt = null;
        PauseReason = null;
    }
}
