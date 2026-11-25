using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain;

namespace Relay.Infrastructure.Persistence;

public sealed class RelayDbContext(DbContextOptions<RelayDbContext> options) : DbContext(options)
{
    public const string IdempotencyIndex = "ux_messages_tenant_id_idempotency_key";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageEvent> MessageEvents => Set<MessageEvent>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<Suppression> Suppressions => Set<Suppression>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Tenant>(ConfigureTenant);
        modelBuilder.Entity<ApiKey>(ConfigureApiKey);
        modelBuilder.Entity<Message>(ConfigureMessage);
        modelBuilder.Entity<MessageEvent>(ConfigureMessageEvent);
        modelBuilder.Entity<OutboxMessage>(ConfigureOutbox);
        modelBuilder.Entity<Suppression>(ConfigureSuppression);
    }

    private static void ConfigureTenant(EntityTypeBuilder<Tenant> b)
    {
        b.Property(t => t.Name).HasMaxLength(100);
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
        b.HasIndex(o => o.MessageId).IsUnique();
        b.HasIndex(o => o.AvailableAt).HasFilter("processed_at IS NULL");
        b.HasOne<Message>().WithMany().HasForeignKey(o => o.MessageId);
    }

    private static void ConfigureSuppression(EntityTypeBuilder<Suppression> b)
    {
        b.Property(s => s.Address).HasMaxLength(Application.EmailAddress.MaxLength);
        b.Property(s => s.Reason).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(s => new { s.TenantId, s.Address }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId);
    }
}
