using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<TranscriptChunk> TranscriptChunks => Set<TranscriptChunk>();
    public DbSet<Feed> Feeds => Set<Feed>();
    public DbSet<Summary> Summaries => Set<Summary>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<PushoverSettings> PushoverSettings => Set<PushoverSettings>();
    public DbSet<DeletedFeedItem> DeletedFeedItems => Set<DeletedFeedItem>();

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
            e.HasIndex(x => new { x.FeedId, x.FeedItemGuid });
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

            // Losing the transcript must not take the job history with it.
            e.HasOne(x => x.Transcript)
                .WithMany()
                .HasForeignKey(x => x.TranscriptId)
                .OnDelete(DeleteBehavior.SetNull);
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

        b.Entity<TranscriptChunk>(e =>
        {
            e.HasIndex(x => new { x.TranscriptId, x.Index }).IsUnique();
            e.HasOne(x => x.Transcript)
                .WithMany(x => x.Chunks)
                .HasForeignKey(x => x.TranscriptId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // One summary per transcript, enforced rather than assumed: re-summarising updates the
        // row it finds, and a unique index is what stops a race between the pipeline and someone
        // pressing the button from quietly leaving two.
        b.Entity<Summary>(e =>
        {
            e.HasIndex(x => x.TranscriptId).IsUnique();
            e.HasOne(x => x.Transcript)
                .WithOne(x => x.Summary)
                .HasForeignKey<Summary>(x => x.TranscriptId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AppUser>(e =>
        {
            // NOCASE, so the unique index really does stop "Brian" and "brian" both existing and
            // then racing over who owns the login. A plain unique index in SQLite is
            // case-sensitive and would happily allow both.
            e.Property(x => x.Username).HasMaxLength(64).UseCollation("NOCASE");
            e.HasIndex(x => x.Username).IsUnique();

            // Stored as the integer, so renumbering the enum would relabel every account.
            e.Property(x => x.Role).HasConversion<int>();
        });

        b.Entity<Feed>(e => e.HasIndex(x => x.RssUrl).IsUnique());

        // Unique so re-deleting an episode that somehow got re-added (or a delete retried after
        // a partial failure) does not throw or double-record the tombstone.
        b.Entity<DeletedFeedItem>(e => e.HasIndex(x => new { x.FeedId, x.FeedItemGuid }).IsUnique());
    }
}
