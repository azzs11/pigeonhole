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

    public static Tenant Create(Guid id, string name, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Tenant { Id = id, Name = name.Trim(), IsActive = true, CreatedAt = now };
    }

    public void Deactivate() => IsActive = false;
}
