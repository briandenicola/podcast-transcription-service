using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<Feed> Feeds => Set<Feed>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Applies to DateTimeOffset and DateTimeOffset? alike.
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUnixMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Episode>(e =>
        {
            e.HasIndex(x => x.AudioSha256);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Feed)
                .WithMany(f => f.Episodes)
                .HasForeignKey(x => x.FeedId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Job>(e =>
        {
            e.HasIndex(x => new { x.State, x.CreatedAt });
            e.HasOne(x => x.Episode)
                .WithMany(x => x.Jobs)
                .HasForeignKey(x => x.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Deliberately one-to-many: re-running an episode on a better model must not
        // destroy the old result.
        b.Entity<Transcript>(e =>
        {
            e.HasIndex(x => x.EpisodeId);
            e.HasOne(x => x.Episode)
                .WithMany(x => x.Transcripts)
                .HasForeignKey(x => x.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Segment>(e =>
        {
            e.HasIndex(x => new { x.TranscriptId, x.Ordinal }).IsUnique();
            e.HasOne(x => x.Transcript)
                .WithMany(x => x.Segments)
                .HasForeignKey(x => x.TranscriptId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Feed>(e => e.HasIndex(x => x.RssUrl).IsUnique());
    }
}
