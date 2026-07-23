using DiscordBot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DiscordBot.Data;

/// <summary>EF Core context backing the bot's SQLite store.</summary>
public sealed class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options)
    {
    }

    public DbSet<GuildConfig> GuildConfigs => Set<GuildConfig>();
    public DbSet<TempVoiceChannel> TempVoiceChannels => Set<TempVoiceChannel>();
    public DbSet<NotificationSubscription> Subscriptions => Set<NotificationSubscription>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<SeenContent> SeenContent => Set<SeenContent>();
    public DbSet<RadioStream> RadioStreams => Set<RadioStream>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Discord snowflakes are unsigned 64-bit. SQLite has no unsigned integer type, and EF's
        // default ulong->long cast is *checked*, so it would throw for ids above long.MaxValue.
        // Reinterpret the bits instead, which round-trips every possible snowflake losslessly.
        configurationBuilder.Properties<ulong>().HaveConversion<UlongToLongConverter>();
        configurationBuilder.Properties<ulong?>().HaveConversion<NullableUlongToLongConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<GuildConfig>(e =>
        {
            e.HasKey(x => x.GuildId);
            e.Property(x => x.GuildId).ValueGeneratedNever();
        });

        b.Entity<TempVoiceChannel>(e =>
        {
            e.HasKey(x => x.ChannelId);
            e.Property(x => x.ChannelId).ValueGeneratedNever();
            e.HasIndex(x => x.GuildId);
        });

        b.Entity<NotificationSubscription>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GuildId, x.SourceType, x.SourceChannelId, x.DiscordChannelId })
                .IsUnique();
        });

        b.Entity<NotificationTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SubscriptionId, x.EventKind }).IsUnique();
        });

        b.Entity<SeenContent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SubscriptionId, x.ExternalId, x.EventKind }).IsUnique();
        });

        b.Entity<RadioStream>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GuildId, x.Name }).IsUnique();
        });
    }
}

/// <summary>Lossless bit-reinterpreting converter between <see cref="ulong"/> and <see cref="long"/>.</summary>
public sealed class UlongToLongConverter : ValueConverter<ulong, long>
{
    public UlongToLongConverter()
        : base(v => unchecked((long)v), v => unchecked((ulong)v))
    {
    }
}

/// <summary>Nullable variant of <see cref="UlongToLongConverter"/>.</summary>
public sealed class NullableUlongToLongConverter : ValueConverter<ulong?, long?>
{
    public NullableUlongToLongConverter()
        : base(
            v => v.HasValue ? unchecked((long)v.Value) : null,
            v => v.HasValue ? unchecked((ulong)v.Value) : null)
    {
    }
}
