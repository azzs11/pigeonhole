using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain;

namespace Relay.Infrastructure.Persistence;

public sealed class RelayDbContext(DbContextOptions<RelayDbContext> options) : DbContext(options)
{
    public const string IdempotencyIndex = "ux_messages_tenant_id_idempotency_key";
    public const string ProviderEventIndex = "ux_provider_events_provider_event_id";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageEvent> MessageEvents => Set<MessageEvent>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<Suppression> Suppressions => Set<Suppression>();
    public DbSet<ProviderEvent> ProviderEvents => Set<ProviderEvent>();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // Count state changes once they are durable: every appended MessageEvent that moves status.
        var moves = ChangeTracker.Entries<MessageEvent>()
            .Where(e => e.State == EntityState.Added && e.Entity.FromStatus != e.Entity.ToStatus)
            .Select(e => e.Entity.ToStatus)
            .ToList();
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        foreach (var to in moves)
        {
            Application.RelayTelemetry.Transitions.Add(1, new KeyValuePair<string, object?>("status", to.ToString()));
        }

        return saved;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Tenant>(ConfigureTenant);
        modelBuilder.Entity<ApiKey>(ConfigureApiKey);
        modelBuilder.Entity<Message>(ConfigureMessage);
        modelBuilder.Entity<MessageEvent>(ConfigureMessageEvent);
        modelBuilder.Entity<OutboxMessage>(ConfigureOutbox);
        modelBuilder.Entity<Suppression>(ConfigureSuppression);
        modelBuilder.Entity<ProviderEvent>(ConfigureProviderEvent);

        // Ids are created in code (Guid v7). Without this, EF treats a new child with a set key
        // (e.g. an appended MessageEvent) as an existing row and issues an UPDATE.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            entity.FindPrimaryKey()?.Properties.Single().ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }
    }

    private static void ConfigureTenant(EntityTypeBuilder<Tenant> b)
    {
        b.Property(t => t.Name).HasMaxLength(100);
        b.Property(t => t.PauseReason).HasMaxLength(500);
        b.Ignore(t => t.IsSendingPaused);
        b.HasIndex(t => t.Name).IsUnique();
    }

    private static void ConfigureApiKey(EntityTypeBuilder<ApiKey> b)
    {
        b.Property(k => k.Name).HasMaxLength(100);
        b.Property(k => k.Prefix).HasMaxLength(Application.ApiKeyFormat.PrefixLength);
        b.Property(k => k.Hash).HasMaxLength(64).IsFixedLength();
        b.Ignore(k => k.IsActive);
        b.HasIndex(k => k.Prefix).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(k => k.TenantId);
    }

    private static void ConfigureMessage(EntityTypeBuilder<Message> b)
    {
        b.Property(m => m.From).HasMaxLength(Application.EmailAddress.MaxLength);
        b.Property(m => m.Subject).HasMaxLength(998);
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(m => m.IdempotencyKey).HasMaxLength(255);
        b.Property(m => m.RequestHash).HasMaxLength(64).IsFixedLength();
        b.Property(m => m.InternetMessageId).HasMaxLength(300);
        b.Property(m => m.ProviderName).HasMaxLength(100);
        b.Property(m => m.ProviderMessageId).HasMaxLength(300);
        b.HasIndex(m => m.InternetMessageId).IsUnique();
        b.HasIndex(m => m.ProviderMessageId);

        // Optimistic concurrency on PostgreSQL's xmin: two workers can never both write one message.
        b.Property<uint>("Version").IsRowVersion();
        b.HasIndex(m => new { m.TenantId, m.IdempotencyKey }).IsUnique().HasDatabaseName(IdempotencyIndex);
        b.HasIndex(m => new { m.TenantId, m.Id }).IsDescending(false, true);
        b.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId);
        b.HasMany(m => m.Events).WithOne().HasForeignKey(e => e.MessageId);
        b.Navigation(m => m.Events).HasField("_events").UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureMessageEvent(EntityTypeBuilder<MessageEvent> b)
    {
        b.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.Detail).HasMaxLength(2000);
        b.HasIndex(e => new { e.MessageId, e.Sequence }).IsUnique();
    }

    private static void ConfigureOutbox(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox");
        b.Property(o => o.LockedBy).HasMaxLength(100);
        b.Property(o => o.LockedBy).IsConcurrencyToken();
        b.Property(o => o.PinnedProvider).HasMaxLength(100);
        b.Property(o => o.TraceParent).HasMaxLength(128);
        b.HasIndex(o => o.MessageId).IsUnique();
        b.HasIndex(o => o.AvailableAt).HasFilter("processed_at IS NULL");
        b.HasOne<Message>().WithMany().HasForeignKey(o => o.MessageId);
    }

    private static void ConfigureProviderEvent(EntityTypeBuilder<ProviderEvent> b)
    {
        b.Property(e => e.Provider).HasMaxLength(50);
        b.Property(e => e.EventId).HasMaxLength(200);
        b.Property(e => e.Kind).HasMaxLength(50);
        b.HasIndex(e => new { e.Provider, e.EventId }).IsUnique().HasDatabaseName(ProviderEventIndex);
    }

    private static void ConfigureSuppression(EntityTypeBuilder<Suppression> b)
    {
        b.Property(s => s.Address).HasMaxLength(Application.EmailAddress.MaxLength);
        b.Property(s => s.Reason).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(s => new { s.TenantId, s.Address }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId);
    }
}
