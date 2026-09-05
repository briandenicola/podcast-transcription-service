using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Services.Chunking;

namespace PodcastTranscription.Tests;

public class ChunkPlannerTests
{
    private static TranscriptionOptions Options(int chunkSeconds = 600, int windowSeconds = 90) => new()
    {
        ChunkSeconds = chunkSeconds,
        SilenceSearchWindowSeconds = windowSeconds,
        MinTailFraction = 0.25
    };

    [Fact]
    public void Audio_shorter_than_one_chunk_is_a_single_chunk()
    {
        var chunks = ChunkPlanner.Plan(300_000, [], Options());

        Assert.Single(chunks);
        Assert.Equal(new AudioChunk(0, 0, 300_000), chunks[0]);
    }

    [Fact]
    public void Chunks_cover_the_whole_episode_without_gaps_or_overlap()
    {
        var chunks = ChunkPlanner.Plan(5_400_000, [], Options());

        Assert.Equal(0, chunks[0].StartMs);
        Assert.Equal(5_400_000, chunks[^1].EndMs);

        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].EndMs, chunks[i].StartMs);
        }
    }

    [Fact]
    public void Without_silences_the_cut_lands_on_the_target()
    {
        var chunks = ChunkPlanner.Plan(1_500_000, [], Options());

        Assert.Equal(600_000, chunks[0].EndMs);
        Assert.Equal(1_200_000, chunks[1].EndMs);
    }

    [Fact]
    public void A_nearby_silence_pulls_the_cut_off_the_target()
    {
        // A pause from 9:50 to 9:54, so words are not severed at the 10:00 mark.
        var silences = new List<SilenceInterval> { new(590_000, 594_000) };

        var chunks = ChunkPlanner.Plan(1_500_000, silences, Options());

        Assert.Equal(592_000, chunks[0].EndMs);
        Assert.Equal(592_000, chunks[1].StartMs);
    }

    [Fact]
    public void The_closest_silence_to_the_target_wins()
    {
        var silences = new List<SilenceInterval>
        {
            new(530_000, 532_000), // 8:50 — inside the window, but far
            new(598_000, 600_000)  // 9:58 — nearly on target
        };

        var chunks = ChunkPlanner.Plan(1_500_000, silences, Options());

        Assert.Equal(599_000, chunks[0].EndMs);
    }

    [Fact]
    public void A_silence_beyond_the_search_window_is_ignored()
    {
        // 7:00 is more than 90 seconds from the 10:00 target.
        var silences = new List<SilenceInterval> { new(420_000, 422_000) };

        var chunks = ChunkPlanner.Plan(1_500_000, silences, Options());

        Assert.Equal(600_000, chunks[0].EndMs);
    }

    [Fact]
    public void A_short_tail_is_absorbed_rather_than_left_as_a_stub()
    {
        // 10 minutes plus 60 seconds: the tail is under a quarter of a chunk.
        var chunks = ChunkPlanner.Plan(660_000, [], Options());

        Assert.Single(chunks);
        Assert.Equal(660_000, chunks[0].EndMs);
    }

    [Fact]
    public void A_tail_worth_keeping_becomes_its_own_chunk()
    {
        var chunks = ChunkPlanner.Plan(900_000, [], Options());

        Assert.Equal(2, chunks.Count);
        Assert.Equal(600_000, chunks[1].StartMs);
        Assert.Equal(900_000, chunks[1].EndMs);
    }

    [Fact]
    public void Indexes_are_sequential_from_zero()
    {
        var chunks = ChunkPlanner.Plan(5_400_000, [], Options());

        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
    }

    [Fact]
    public void The_plan_is_deterministic_so_a_resumed_job_cuts_identically()
    {
        var silences = new List<SilenceInterval> { new(590_000, 594_000), new(1_180_000, 1_183_000) };

        Assert.Equal(
            ChunkPlanner.Plan(2_000_000, silences, Options()),
            ChunkPlanner.Plan(2_000_000, silences, Options()));
    }

    [Fact]
    public void Empty_audio_produces_no_chunks() =>
        Assert.Empty(ChunkPlanner.Plan(0, [], Options()));
}
