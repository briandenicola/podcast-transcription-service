using PodcastTranscription.Web.Services.Lifestore;

namespace PodcastTranscription.Tests;

public class PassageGrouperTests
{
    [Fact]
    public void Breaks_between_segments_once_a_passage_is_long_enough()
    {
        var passages = PassageGrouper.Group(
        [
            (0, 1000, "aaaa"),
            (1000, 2000, " bbbb "),
            (2000, 3000, ""),
            (3000, 4000, "cccc"),
            (4000, 5000, "dd"),
        ], targetChars: 9);

        Assert.Equal(
        [
            new Passage(0, 2000, "aaaa bbbb"),
            new Passage(3000, 5000, "cccc dd"),
        ], passages);
    }

    [Fact]
    public void No_segments_make_no_passages()
    {
        Assert.Empty(PassageGrouper.Group([]));
        Assert.Empty(PassageGrouper.Group([(0, 10, "   ")]));
    }
}
