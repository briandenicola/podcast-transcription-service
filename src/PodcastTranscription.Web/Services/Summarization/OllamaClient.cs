using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;

namespace PodcastTranscription.Web.Services.Summarization;

/// <summary>
/// Typed client over Ollama's HTTP API. Only two routes are used: <c>/api/tags</c> to see what
/// the server has, and <c>/api/generate</c> to ask it something. Streaming is deliberately off —
/// nothing here renders tokens as they arrive, and a single response is far easier to retry.
/// </summary>
public class OllamaClient(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaClient> log)
{
    private readonly OllamaOptions _options = options.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Model => _options.Model;
    public string Endpoint => _options.BaseUrl;
    public bool Enabled => _options.Enabled;

    /// <summary>Sends one prompt and returns the whole answer.</summary>
    public async Task<OllamaCompletion> GenerateAsync(
        string system, string prompt, CancellationToken ct = default)
    {
        var request = new
        {
            model = _options.Model,
            prompt,
            system,
            stream = false,
            options = new
            {
                temperature = _options.Temperature,
                num_ctx = _options.ContextTokens,
                num_predict = _options.MaxOutputTokens
            }
        };

        var body = new StringContent(
            JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        log.LogDebug("Asking {Model} at {Endpoint} for {Chars:N0} characters of prompt",
            _options.Model, _options.BaseUrl.TrimEnd('/'), prompt.Length);

        using var response = await http.PostAsync("/api/generate", body, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // A model that was never pulled is the overwhelmingly common cause, and Ollama says
            // so in the body. Passing it through saves an hour of looking at the wrong thing.
            throw new OllamaException(
                $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(raw, 500)}");
        }

        var parsed = JsonSerializer.Deserialize<GenerateResponse>(raw, JsonOptions)
            ?? throw new OllamaException("Ollama returned a body that did not parse as JSON.");

        if (string.IsNullOrWhiteSpace(parsed.Response))
        {
            throw new OllamaException("Ollama returned an empty completion.");
        }

        log.LogDebug("{Model} answered in {Elapsed} ({Tokens} tokens)",
            _options.Model, sw.Elapsed, parsed.EvalCount);

        return new OllamaCompletion(
            parsed.Response.Trim(), parsed.PromptEvalCount, parsed.EvalCount, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Asks Ollama what it has. <c>/api/tags</c> answers both questions worth asking before a job
    /// runs: whether the server is there at all, and whether the configured model is actually
    /// pulled — an unpulled model is reachable and still fails every single request.
    /// </summary>
    public async Task<OllamaHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new OllamaHealth(false, false, "disabled", []);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await http.GetAsync("/api/tags", cts.Token);
            var raw = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new OllamaHealth(true, false, $"HTTP {(int)response.StatusCode}", []);
            }

            var models = ParseModelNames(raw);
            var available = models.Any(m => NamesMatch(m, _options.Model));

            return new OllamaHealth(
                true,
                available,
                available ? "ok" : $"model '{_options.Model}' is not pulled",
                models);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Ollama at {Endpoint} is not reachable", _options.BaseUrl);
            return new OllamaHealth(false, false, "unreachable", []);
        }
    }

    /// <summary>
    /// Ollama stores every model under an explicit tag, so a configured <c>llama3.1</c> has to
    /// match the <c>llama3.1:latest</c> that <c>ollama pull llama3.1</c> actually created.
    /// </summary>
    internal static bool NamesMatch(string listed, string configured)
    {
        if (listed.Equals(configured, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var listedTagged = listed.Contains(':') ? listed : listed + ":latest";
        var configuredTagged = configured.Contains(':') ? configured : configured + ":latest";

        return listedTagged.Equals(configuredTagged, StringComparison.OrdinalIgnoreCase);
    }

    internal static List<string> ParseModelNames(string body)
    {
        var names = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return names;
            }

            foreach (var model in models.EnumerateArray())
            {
                if (model.TryGetProperty("name", out var name) && name.GetString() is { } value)
                {
                    names.Add(value);
                }
            }
        }
        catch (JsonException)
        {
            // An answer we cannot read still proves something is listening, which is what the
            // caller is really asking. Report no models rather than throwing.
        }

        return names;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private sealed record GenerateResponse
    {
        public string? Response { get; init; }

        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; init; }

        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; init; }
    }
}

/// <summary>One answer, with what it cost.</summary>
public record OllamaCompletion(string Text, int? PromptTokens, int? CompletionTokens, long ElapsedMs);

/// <summary>
/// What Ollama reported. <see cref="ModelAvailable"/> is the one worth acting on: the server can
/// be up and still reject every request because the configured model was never pulled.
/// </summary>
public record OllamaHealth(bool Reachable, bool ModelAvailable, string Status, IReadOnlyList<string> Models)
{
    /// <summary>Up, and holding the model we intend to ask for.</summary>
    public bool Ready => Reachable && ModelAvailable;
}

public class OllamaException(string message, Exception? inner = null) : Exception(message, inner);
