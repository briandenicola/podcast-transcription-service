using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PodcastTranscription.Web.Data;

/// <summary>
/// SQLite has no native date type and refuses to ORDER BY or compare a <see cref="DateTimeOffset"/>.
/// Storing them as Unix milliseconds makes them sortable integers in the database while the domain
/// keeps working in <see cref="DateTimeOffset"/>. Everything the app writes is UTC, so the offset
/// that is lost on the round trip carries no information.
/// </summary>
public class DateTimeOffsetToUnixMillisecondsConverter()
    : ValueConverter<DateTimeOffset, long>(
        v => v.ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v));
