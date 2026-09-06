using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;

namespace PodcastTranscription.Web.Services;

/// <summary>
/// Typed client over whisper.cpp's <c>whisper-server</c>. The server handles one request at a
/// time and returns nothing until the whole file is done, so callers are expected to post
/// bounded chunks rather than whole episodes once M2 lands.
/// </summary>
public class WhisperClient(HttpClient http, IOptions<WhisperOptions> options, ILogger<WhisperClient> log)
{
    private readonly WhisperOptions _options = options.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string Model => _options.Model;
    public string Endpoint => _options.BaseUrl;

    /// <summary>Posts a WAV file to <c>/inference</c> and returns the parsed response plus its raw body.</summary>
    public async Task<(WhisperResponse Response, string RawJson)> TranscribeAsync(
        string wavPath, WhisperRequest? request = null, CancellationToken ct = default)
    {
        request ??= new WhisperRequest();

        await using var file = File.OpenRead(wavPath);
        using var content = new MultipartFormDataContent();

        var audio = new StreamContent(file);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", Path.GetFileName(wavPath));

        // verbose_json is the only format that carries segment timings and avg_logprob.
        content.Add(new StringContent("verbose_json"), "response_format");

        var language = request.Language ?? _options.Language;
        if (!string.IsNullOrWhiteSpace(language))
        {
            content.Add(new StringContent(language), "language");
        }

        var prompt = request.Prompt ?? _options.Prompt;
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            content.Add(new StringContent(prompt), "prompt");
        }

        var temperature = request.Temperature ?? _options.Temperature;
        content.Add(new StringContent(temperature.ToString(CultureInfo.InvariantCulture)), "temperature");

        // Decoding parameters are sent explicitly rather than left to the server's defaults.
        // These are what stand between a difficult passage and a repetition loop, and leaving
        // them implicit means they change under you when the server is upgraded or restarted
        // with different flags.
        Add("temperature_inc", _options.TemperatureIncrement);
        Add("entropy_thold", _options.EntropyThreshold);
        Add("logprob_thold", _options.LogProbThreshold);
        Add("no_speech_thold", _options.NoSpeechThreshold);
        Add("max_context", _options.MaxContext);
        Add("beam_size", _options.BeamSize);
        Add("best_of", _options.BestOf);

        if (_options.SuppressNonSpeechTokens)
        {
            content.Add(new StringContent("true"), "suppress_nst");
        }

        if (request.MaxLen is { } maxLen)
        {
            content.Add(new StringContent(maxLen.ToString(CultureInfo.InvariantCulture)), "max_len");
        }

        if (request.SplitOnWord)
        {
            content.Add(new StringContent("true"), "split_on_word");
        }

        void Add(string name, double value) =>
            content.Add(new StringContent(value.ToString(CultureInfo.InvariantCulture)), name);

        var sw = Stopwatch.StartNew();
        log.LogInformation("Posting {File} ({Bytes:N0} bytes) to {Endpoint}/inference",
            Path.GetFileName(wavPath), file.Length, _options.BaseUrl.TrimEnd('/'));

        using var response = await http.PostAsync("/inference", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new WhisperException(
                $"whisper-server returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}");
        }

        var parsed = JsonSerializer.Deserialize<WhisperResponse>(body, JsonOptions)
            ?? throw new WhisperException("whisper-server returned a body that did not parse as JSON.");

        log.LogInformation("Inference finished in {Elapsed} with {Segments} segments",
            sw.Elapsed, parsed.Segments.Count);

        return (parsed, body);
    }

    /// <summary>
    /// Asks whisper-server how it is. Its <c>/health</c> route distinguishes a server that is up
    /// from one still reading a model off disk — a large model takes a while, and treating
    /// "loading" as ready means jobs fail and burn their retries for no reason.
    /// </summary>
    public async Task<WhisperHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await http.GetAsync("/health", cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            // Older builds have no /health at all. Any HTTP answer still means the process is
            // listening, so fall back to treating it as up rather than reporting it down.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new WhisperHealth(true, false, "up (no /health route)");
            }

            var status = ReadStatus(body);
            var loading = status?.Contains("loading", StringComparison.OrdinalIgnoreCase) == true;

            return new WhisperHealth(true, loading, status ?? "ok");
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "whisper-server at {Endpoint} is not reachable", _options.BaseUrl);
            return new WhisperHealth(false, false, "unreachable");
        }
    }

    /// <summary>True when the server answers at all, whatever it is doing.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default) =>
        (await CheckHealthAsync(ct)).Reachable;

    private static string? ReadStatus(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>
/// What whisper-server reported. <see cref="ModelLoading"/> is the state worth acting on: the
/// server is there, but a request sent now would fail.
/// </summary>
public record WhisperHealth(bool Reachable, bool ModelLoading, string Status)
{
    /// <summary>Up and able to take work.</summary>
    public bool Ready => Reachable && !ModelLoading;
}

public class WhisperException(string message, Exception? inner = null) : Exception(message, inner);
