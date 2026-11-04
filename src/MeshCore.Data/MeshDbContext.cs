using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeshCore.Data;

public sealed class MeshDbContext(DbContextOptions<MeshDbContext> options) : DbContext(options)
{
    public DbSet<ContactEntity> Contacts => Set<ContactEntity>();

    public DbSet<ChannelEntity> Channels => Set<ChannelEntity>();

    public DbSet<MessageEntity> Messages => Set<MessageEntity>();

    public DbSet<ReadMarkerEntity> ReadMarkers => Set<ReadMarkerEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't compare or sort DateTimeOffset; store as Unix milliseconds (UTC) so it can.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UnixMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContactEntity>(e =>
        {
            e.ToTable("Contacts");
            e.HasKey(c => c.PublicKey);
            e.Property(c => c.PublicKey).HasMaxLength(64);
            e.Property(c => c.KeyPrefix).HasMaxLength(12);
            e.Property(c => c.Name).HasMaxLength(32);
            e.HasIndex(c => c.KeyPrefix);
        });

        modelBuilder.Entity<ChannelEntity>(e =>
        {
            e.ToTable("Channels");
            e.HasKey(c => c.Index);
            e.Property(c => c.Index).ValueGeneratedNever();
            e.Property(c => c.Name).HasMaxLength(32);
        });

        modelBuilder.Entity<MessageEntity>(e =>
        {
            e.ToTable("Messages");
            e.Property(m => m.ContactKeyPrefix).HasMaxLength(12);
            e.Property(m => m.SenderName).HasMaxLength(64);
            e.Property(m => m.SignerPrefix).HasMaxLength(16);
            e.Property(m => m.Text).HasMaxLength(512);
            e.Property(m => m.SentByUserId).HasMaxLength(64);
            e.Property(m => m.Error).HasMaxLength(256);
            e.HasIndex(m => new { m.Kind, m.ContactKeyPrefix, m.ReceivedAt });
            e.HasIndex(m => new { m.Kind, m.ChannelIndex, m.ReceivedAt });
            e.HasIndex(m => m.ExpectedAck);
        });

        modelBuilder.Entity<ReadMarkerEntity>(e =>
        {
            e.ToTable("ReadMarkers");
            e.HasKey(r => new { r.UserId, r.ConversationKey });
            e.Property(r => r.UserId).HasMaxLength(64);
            e.Property(r => r.ConversationKey).HasMaxLength(16);
        });
    }

    private sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
        v => v.ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v));
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without starting the app.</summary>
internal sealed class DesignTimeMeshDbContextFactory : IDesignTimeDbContextFactory<MeshDbContext>
{
    public MeshDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MeshDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
