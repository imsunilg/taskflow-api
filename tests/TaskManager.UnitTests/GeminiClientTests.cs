using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskManager.Application.AI;
using TaskManager.Infrastructure.AI;

namespace TaskManager.UnitTests;

public class GeminiClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException());
    }

    private static GeminiClient MakeClient(HttpMessageHandler handler, string apiKey = "test-key") =>
        new(new HttpClient(handler), Options.Create(new GeminiOptions { ApiKey = apiKey, TimeoutSeconds = 5 }),
            NullLogger<GeminiClient>.Instance);

    [Fact]
    public async Task GenerateJsonAsync_parses_a_well_formed_response_and_reads_usage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "candidates": [ { "content": { "parts": [ { "text": "{\"title\":\"Buy milk\"}" } ] } } ],
                  "usageMetadata": { "promptTokenCount": 12, "candidatesTokenCount": 4 }
                }
                """),
        });
        var client = MakeClient(handler);

        var result = await client.GenerateJsonAsync("system", "user", CancellationToken.None);

        result.RawText.Should().Contain("Buy milk");
        result.InputTokens.Should().Be(12);
        result.OutputTokens.Should().Be(4);
    }

    [Fact]
    public async Task GenerateJsonAsync_throws_AIServiceException_when_gemini_returns_no_candidates()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "candidates": [] }"""),
        });
        var client = MakeClient(handler);

        var act = () => client.GenerateJsonAsync("system", "user", CancellationToken.None);

        await act.Should().ThrowAsync<AIServiceException>();
    }

    [Fact]
    public async Task GenerateJsonAsync_throws_AIServiceException_when_the_body_is_not_valid_json()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("this is not json"),
        });
        var client = MakeClient(handler);

        var act = () => client.GenerateJsonAsync("system", "user", CancellationToken.None);

        await act.Should().ThrowAsync<AIServiceException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GenerateJsonAsync_throws_AIServiceUnavailableException_on_upstream_failure(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("error") });
        var client = MakeClient(handler);

        var act = () => client.GenerateJsonAsync("system", "user", CancellationToken.None);

        await act.Should().ThrowAsync<AIServiceUnavailableException>();
    }

    [Fact]
    public async Task GenerateJsonAsync_throws_AIServiceException_on_a_non_transient_4xx()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad request") });
        var client = MakeClient(handler);

        var act = () => client.GenerateJsonAsync("system", "user", CancellationToken.None);

        await act.Should().ThrowAsync<AIServiceException>();
    }

    [Fact]
    public async Task GenerateJsonAsync_throws_AIServiceUnavailableException_on_timeout()
    {
        var client = MakeClient(new ThrowingHandler());

        var act = () => client.GenerateJsonAsync("system", "user", CancellationToken.None);

        await act.Should().ThrowAsync<AIServiceUnavailableException>();
    }

    [Fact]
    public async Task GenerateJsonAsync_throws_AIServiceUnavailableException_when_no_api_key_is_configured()
    {
        var client = MakeClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), apiKey: "");

        var act = () => client.GenerateJsonAsync("system", "user", CancellationToken.None);

        await act.Should().ThrowAsync<AIServiceUnavailableException>();
        client.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateWithToolsAsync_returns_a_parsed_function_call()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "candidates": [ { "content": { "parts": [
                    { "functionCall": { "name": "GetTodayTasks", "args": {} } }
                  ] } } ],
                  "usageMetadata": { "promptTokenCount": 20, "candidatesTokenCount": 5 }
                }
                """),
        });
        var client = MakeClient(handler);

        var result = await client.GenerateWithToolsAsync(
            "system", Array.Empty<(string, string)>(), "what's on today?",
            new[] { new GeminiToolDeclaration("GetTodayTasks", "list today's tasks", new System.Text.Json.Nodes.JsonObject()) },
            CancellationToken.None);

        result.ToolName.Should().Be("GetTodayTasks");
        result.TextReply.Should().BeNull();
    }

    [Fact]
    public async Task GenerateWithToolsAsync_returns_plain_text_when_gemini_does_not_call_a_tool()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "candidates": [ { "content": { "parts": [ { "text": "Hi there!" } ] } } ] }
                """),
        });
        var client = MakeClient(handler);

        var result = await client.GenerateWithToolsAsync(
            "system", Array.Empty<(string, string)>(), "hello",
            Array.Empty<GeminiToolDeclaration>(), CancellationToken.None);

        result.ToolName.Should().BeNull();
        result.TextReply.Should().Be("Hi there!");
    }
}
