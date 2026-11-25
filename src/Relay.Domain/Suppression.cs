namespace Relay.Domain;

public enum SuppressionReason
{
    HardBounce = 0,
    Complaint = 1,
    Manual = 2,
}

/// <summary>An address a tenant must not send to. Populated from provider events in Phase 3.</summary>
public sealed class Suppression
{
    private Suppression()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Address { get; private set; } = string.Empty;
    public SuppressionReason Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Suppression Create(Guid id, Guid tenantId, string address, SuppressionReason reason, DateTimeOffset now) =>
        new() { Id = id, TenantId = tenantId, Address = address, Reason = reason, CreatedAt = now };
}
