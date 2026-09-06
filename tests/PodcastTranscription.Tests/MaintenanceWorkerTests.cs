using PodcastTranscription.Web.Services.Maintenance;

namespace PodcastTranscription.Tests;

public class MaintenanceWorkerTests
{
    [Theory]
    [InlineData("03:30", 3, 30)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    public void A_valid_time_is_parsed(string input, int hour, int minute) =>
        Assert.Equal(new TimeOnly(hour, minute), MaintenanceWorker.ParseRunAt(input));

    /// <summary>A malformed setting must not stop the app booting.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3:30am")]
    [InlineData("25:00")]
    [InlineData("nonsense")]
    public void A_malformed_time_falls_back_rather_than_throwing(string? input) =>
        Assert.Equal(new TimeOnly(3, 30), MaintenanceWorker.ParseRunAt(input));

    [Fact]
    public void The_wait_is_until_later_today_when_the_hour_has_not_passed()
    {
        var now = new DateTime(2026, 3, 1, 1, 0, 0);

        Assert.Equal(TimeSpan.FromHours(2.5), MaintenanceWorker.TimeUntilNext(new TimeOnly(3, 30), now));
    }

    [Fact]
    public void The_wait_rolls_to_tomorrow_once_the_hour_has_passed()
    {
        var now = new DateTime(2026, 3, 1, 4, 0, 0);

        Assert.Equal(TimeSpan.FromHours(23.5), MaintenanceWorker.TimeUntilNext(new TimeOnly(3, 30), now));
    }

    [Fact]
    public void Exactly_at_the_hour_waits_a_full_day_rather_than_firing_twice()
    {
        var now = new DateTime(2026, 3, 1, 3, 30, 0);

        Assert.Equal(TimeSpan.FromDays(1), MaintenanceWorker.TimeUntilNext(new TimeOnly(3, 30), now));
    }
}
