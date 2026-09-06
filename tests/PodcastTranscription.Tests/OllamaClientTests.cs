using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Services.Summarization;

namespace PodcastTranscription.Tests;

public class OllamaClientTests
{
    private const string GenerateResponse = """
    {
      "model": "llama3.1:8b",
      "response": "  ## Overview\nThey argue about interest rates.  ",
      "done": true,
      "prompt_eval_count": 1840,
      "eval_count": 320
    }
    """;

    private const string TagsResponse = """
    {
      "models": [
        { "name": "llama3.1:8b", "size": 4661224676 },
        { "name": "nomic-embed-text:latest", "size": 274302450 }
      ]
    }
    """;

    private static OllamaClient CreateClient(HttpMessageHandler handler, OllamaOptions? options = null)
    {
        options ??= new OllamaOptions
        {
            Enabled = true,
            BaseUrl = "http://ollama.test:11434",
            Model = "llama3.1:8b"
        };

        var http = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
        return new OllamaClient(http, Options.Create(options), NullLogger<OllamaClient>.Instance);
    }

    [Fact]
    public async Task GenerateAsync_returns_the_trimmed_completion_and_its_token_counts()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, GenerateResponse);

        var completion = await CreateClient(handler).GenerateAsync("be terse", "summarise this");

        Assert.Equal("## Overview\nThey argue about interest rates.", completion.Text);
        Assert.Equal(1840, completion.PromptTokens);
        Assert.Equal(320, completion.CompletionTokens);
    }

    [Fact]
    public async Task GenerateAsync_sends_the_decoding_options_rather_than_leaving_them_to_the_server()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, GenerateResponse);
        var options = new OllamaOptions
        {
            Enabled = true,
            BaseUrl = "http://ollama.test:11434",
            Model = "mistral:7b",
            Temperature = 0.35,
            ContextTokens = 16384,
            MaxOutputTokens = 900
        };

        await CreateClient(handler, options).GenerateAsync("system text", "prompt text");

        var body = handler.LastRequestBody!;
        Assert.Contains("\"model\":\"mistral:7b\"", body);
        Assert.Contains("\"system\":\"system text\"", body);

        // num_ctx above all: Ollama's own default is 2048, which truncates an episode silently.
        Assert.Contains("\"num_ctx\":16384", body);
        Assert.Contains("\"num_predict\":900", body);
        Assert.Contains("\"temperature\":0.35", body);

        // Nothing on this path renders tokens as they arrive, and a streamed body would not parse.
        Assert.Contains("\"stream\":false", body);
    }

    [Fact]
    public async Task GenerateAsync_passes_the_server_error_through_because_it_names_the_cause()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.NotFound, """{"error":"model 'llama3.1:8b' not found, try pulling it first"}""");

        var ex = await Assert.ThrowsAsync<OllamaException>(
            () => CreateClient(handler).GenerateAsync("s", "p"));

        Assert.Contains("try pulling it first", ex.Message);
    }

    [Fact]
    public async Task GenerateAsync_rejects_an_empty_completion()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"response":"   ","done":true}""");

        var ex = await Assert.ThrowsAsync<OllamaException>(
            () => CreateClient(handler).GenerateAsync("s", "p"));

        Assert.Equal("Ollama returned an empty completion.", ex.Message);
    }

    // ------------------------------------------------------- reasoning models --

    [Fact]
    public async Task Thinking_is_switched_off_explicitly_because_ollama_turns_it_on_by_itself()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, GenerateResponse);

        await CreateClient(handler).GenerateAsync("s", "p");

        // Absent, Ollama defaults thinking to on for any model that supports it, and the model
        // then spends its whole output budget reasoning and answers with nothing at all.
        Assert.Contains("\"think\":false", handler.LastRequestBody);
    }

    [Fact]
    public async Task Thinking_can_be_turned_back_on()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, GenerateResponse);
        var options = new OllamaOptions
        {
            Enabled = true,
            BaseUrl = "http://ollama.test:11434",
            Model = "qwen3:8b",
            Think = true
        };

        await CreateClient(handler, options).GenerateAsync("s", "p");

        Assert.Contains("\"think\":true", handler.LastRequestBody);
    }

    [Fact]
    public async Task Reasoning_returned_in_its_own_field_with_no_answer_names_the_cause()
    {
        // Exactly what qwen3 does when it runs out of budget mid-thought: HTTP 200, a full
        // chain of thought, and nothing in response.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """
        {
          "model": "qwen3:8b",
          "response": "",
          "thinking": "Okay, the user wants a summary. Let me read the transcript carefully...",
          "done": true,
          "eval_count": 2048
        }
        """);

        var options = new OllamaOptions
        {
            Enabled = true,
            BaseUrl = "http://ollama.test:11434",
            Model = "qwen3:8b",
            MaxOutputTokens = 2048
        };

        var ex = await Assert.ThrowsAsync<OllamaException>(
            () => CreateClient(handler, options).GenerateAsync("s", "p"));

        // "Empty completion" is the symptom; the message has to carry the remedy.
        Assert.Contains("qwen3:8b", ex.Message);
        Assert.Contains("reasoning and no answer", ex.Message);
        Assert.Contains("MaxOutputTokens", ex.Message);
    }

    [Fact]
    public async Task Reasoning_left_inline_in_the_answer_is_stripped_rather_than_summarised()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """
        {
          "response": "<think>Let me work through this.</think>\n## Overview\nThey argue about rates.",
          "done": true
        }
        """);

        var completion = await CreateClient(handler).GenerateAsync("s", "p");

        Assert.Equal("## Overview\nThey argue about rates.", completion.Text);
        Assert.DoesNotContain("think", completion.Text);
    }

    [Fact]
    public async Task An_answer_that_is_only_an_unclosed_think_block_is_not_treated_as_a_summary()
    {
        // The budget ran out mid-thought, so the tag never closed. Without this the "summary"
        // would be the model's reasoning about how to write one.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """
        {"response":"<think>First I should identify the speakers and then","done":true}
        """);

        var ex = await Assert.ThrowsAsync<OllamaException>(
            () => CreateClient(handler).GenerateAsync("s", "p"));

        Assert.Contains("reasoning and no answer", ex.Message);
    }

    [Theory]
    [InlineData("<think>reasoning</think>answer", "answer")]
    [InlineData("<think>a</think>\n\n  answer  ", "answer")]
    [InlineData("no tags here", "no tags here")]
    [InlineData("<think>only reasoning</think>", "")]
    [InlineData("<think>never closed", "")]
    [InlineData("answer first<think>then reasoning", "answer first")]
    [InlineData(null, "")]
    public void StripThinking_keeps_only_the_answer(string? response, string expected) =>
        Assert.Equal(expected, OllamaClient.StripThinking(response));

    [Fact]
    public async Task CheckHealthAsync_is_ready_when_the_configured_model_is_pulled()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TagsResponse);

        var health = await CreateClient(handler).CheckHealthAsync();

        Assert.True(health.Reachable);
        Assert.True(health.ModelAvailable);
        Assert.True(health.Ready);
        Assert.Contains("nomic-embed-text:latest", health.Models);
    }

    [Fact]
    public async Task CheckHealthAsync_reports_a_reachable_server_that_lacks_the_model()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TagsResponse);
        var options = new OllamaOptions
        {
            Enabled = true,
            BaseUrl = "http://ollama.test:11434",
            Model = "qwen2.5:32b"
        };

        var health = await CreateClient(handler, options).CheckHealthAsync();

        // The distinction that matters: up, and still going to fail every single request.
        Assert.True(health.Reachable);
        Assert.False(health.ModelAvailable);
        Assert.False(health.Ready);
        Assert.Contains("qwen2.5:32b", health.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_does_not_call_out_at_all_when_summaries_are_off()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TagsResponse);
        var options = new OllamaOptions { Enabled = false, BaseUrl = "http://ollama.test:11434" };

        var health = await CreateClient(handler, options).CheckHealthAsync();

        Assert.False(health.Reachable);
        Assert.Equal("disabled", health.Status);
        Assert.Null(handler.LastRequest);
    }

    [Theory]
    // `ollama pull llama3.1` stores llama3.1:latest, so a config without a tag has to match it.
    [InlineData("llama3.1:latest", "llama3.1", true)]
    [InlineData("llama3.1", "llama3.1:latest", true)]
    [InlineData("llama3.1:8b", "llama3.1:8b", true)]
    [InlineData("LLAMA3.1:8B", "llama3.1:8b", true)]
    [InlineData("llama3.1:8b", "llama3.1", false)]
    [InlineData("llama3.1:8b", "llama3.2:8b", false)]
    public void NamesMatch_treats_an_untagged_name_as_latest(string listed, string configured, bool expected) =>
        Assert.Equal(expected, OllamaClient.NamesMatch(listed, configured));

    [Fact]
    public void ParseModelNames_returns_nothing_rather_than_throwing_on_a_body_it_cannot_read() =>
        Assert.Empty(OllamaClient.ParseModelNames("<html>not ollama</html>"));
}
