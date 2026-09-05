using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Services;

namespace PodcastTranscription.Tests;

public class WhisperClientTests : IDisposable
{
    private const string VerboseJson = """
    {
      "task": "transcribe",
      "language": "en",
      "duration": 12.5,
      "text": " Hello there. General Kenobi.",
      "segments": [
        { "id": 0, "start": 0.0,  "end": 2.24, "text": " Hello there.",     "avg_logprob": -0.21 },
        { "id": 1, "start": 2.24, "end": 5.5,  "text": " General Kenobi.",  "avg_logprob": -0.44 }
      ]
    }
    """;

    private readonly string _wavPath = Path.Combine(Path.GetTempPath(), $"whisper-test-{Guid.NewGuid():N}.wav");

    public WhisperClientTests() => File.WriteAllBytes(_wavPath, new byte[64]);

    public void Dispose() => File.Delete(_wavPath);

    private static WhisperClient CreateClient(StubHttpMessageHandler handler, WhisperOptions? options = null)
    {
        options ??= new WhisperOptions { BaseUrl = "http://whisper.test:8080", Language = "en" };
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
        return new WhisperClient(http, Options.Create(options), NullLogger<WhisperClient>.Instance);
    }

    [Fact]
    public async Task TranscribeAsync_parses_segments_and_converts_seconds_to_milliseconds()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, VerboseJson);
        var client = CreateClient(handler);

        var (response, raw) = await client.TranscribeAsync(_wavPath);

        Assert.Equal("en", response.Language);
        Assert.Equal(12.5, response.Duration);
        Assert.Equal(2, response.Segments.Count);

        Assert.Equal(0, response.Segments[0].StartMs);
        Assert.Equal(2240, response.Segments[0].EndMs);
        Assert.Equal(2240, response.Segments[1].StartMs);
        Assert.Equal(5500, response.Segments[1].EndMs);
        Assert.Equal(-0.44, response.Segments[1].AvgLogProb);

        // RawJson is kept verbatim so segments can be re-derived without re-running inference.
        Assert.Equal(VerboseJson, raw);
    }

    [Fact]
    public async Task TranscribeAsync_posts_to_inference_requesting_verbose_json()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, VerboseJson);
        var client = CreateClient(handler);

        await client.TranscribeAsync(_wavPath);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://whisper.test:8080/inference", handler.LastRequest.RequestUri!.ToString());

        // Plain "json" returns only a flat text field, with no timings to build segments from.
        Assert.Contains("verbose_json", handler.LastRequestBody);
        Assert.Contains("name=response_format", handler.LastRequestBody);
    }

    [Fact]
    public async Task TranscribeAsync_sends_word_timestamp_parameters_when_asked()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, VerboseJson);
        var client = CreateClient(handler);

        await client.TranscribeAsync(_wavPath, new WhisperRequest { MaxLen = 1, SplitOnWord = true });

        Assert.Contains("name=max_len", handler.LastRequestBody);
        Assert.Contains("name=split_on_word", handler.LastRequestBody);
    }

    [Fact]
    public async Task TranscribeAsync_uses_the_per_request_prompt_over_the_configured_one()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, VerboseJson);
        var client = CreateClient(handler, new WhisperOptions
        {
            BaseUrl = "http://whisper.test:8080",
            Prompt = "configured prompt"
        });

        await client.TranscribeAsync(_wavPath, new WhisperRequest { Prompt = "Kenobi, Skywalker" });

        Assert.Contains("Kenobi, Skywalker", handler.LastRequestBody);
        Assert.DoesNotContain("configured prompt", handler.LastRequestBody);
    }

    [Fact]
    public async Task TranscribeAsync_throws_with_the_server_body_when_the_request_fails()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "model not loaded");
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<WhisperException>(() => client.TranscribeAsync(_wavPath));

        Assert.Contains("500", ex.Message);
        Assert.Contains("model not loaded", ex.Message);
    }
}
