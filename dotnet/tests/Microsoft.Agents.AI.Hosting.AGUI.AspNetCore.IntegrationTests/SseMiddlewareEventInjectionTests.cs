// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Agents.AI.AGUI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.IntegrationTests;

/// <summary>
/// Option 1: SSE stream interceptor.
///
/// Wraps Response.Body before the endpoint runs. Buffers incoming bytes, detects
/// SSE event boundaries (\n\n), forwards each complete event to the real stream,
/// and optionally injects additional events immediately after.
///
/// This is purely consumer code — no internal framework types are used.
/// </summary>
[SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
    Justification = "_inner is the original response body stream — we do not own it")]
internal sealed class SseInterceptorStream : Stream
{
    private readonly Stream _inner;
    private readonly Func<string, IEnumerable<string>> _injector;
    private readonly MemoryStream _buffer = new();

    public SseInterceptorStream(Stream inner, Func<string, IEnumerable<string>> injector)
    {
        _inner = inner;
        _injector = injector;
    }

    // ── Stream contract ──────────────────────────────────────────────────────

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    // SseFormatter only uses the async overload; synchronous Write is not expected
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use WriteAsync instead.");

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _buffer.Write(buffer.Span);
        await ForwardCompleteEventsAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _buffer.Dispose();
        base.Dispose(disposing);
    }

    // ── Core logic ───────────────────────────────────────────────────────────

    private async Task ForwardCompleteEventsAsync(CancellationToken cancellationToken)
    {
        // SSE events are delimited by \n\n.  A single WriteAsync call may carry
        // zero, one, or several complete events — buffer until we have at least one.
        string text = Encoding.UTF8.GetString(_buffer.ToArray());

        int lastBoundary = -1;
        int pos = 0;
        while ((pos = text.IndexOf("\n\n", pos, StringComparison.Ordinal)) >= 0)
        {
            lastBoundary = pos + 2;
            pos += 2;
        }

        if (lastBoundary < 0) return; // no complete event yet

        string complete = text[..lastBoundary];
        string remaining = text[lastBoundary..];

        _buffer.SetLength(0);
        if (remaining.Length > 0)
            _buffer.Write(Encoding.UTF8.GetBytes(remaining));

        // Process each complete event in order
        foreach (string evt in complete.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            // Forward the original event
            await _inner.WriteAsync(Encoding.UTF8.GetBytes(evt + "\n\n"), cancellationToken)
                .ConfigureAwait(false);

            // Ask the injector if it wants to emit additional events after this one
            if (!evt.StartsWith("data: ", StringComparison.Ordinal)) continue;

            string json = evt["data: ".Length..];
            foreach (string injected in _injector(json))
            {
                await _inner.WriteAsync(
                    Encoding.UTF8.GetBytes($"data: {injected}\n\n"),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// ASP.NET Core middleware that uses <see cref="SseInterceptorStream"/> to inject
/// AG-UI events into the SSE response produced by <c>MapAGUI</c>.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by ASP.NET Core via UseMiddleware<T>")]
internal sealed class SseEventInjectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Only intercept the agent endpoint
        if (!context.Request.Path.StartsWithSegments("/agent"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        Stream originalBody = context.Response.Body;
        using var interceptor = new SseInterceptorStream(originalBody, InjectAfter);
        context.Response.Body = interceptor;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    // Inject a complete text-message sequence immediately after RUN_STARTED
    private static IEnumerable<string> InjectAfter(string eventJson)
    {
        using JsonDocument doc = JsonDocument.Parse(eventJson);
        if (!doc.RootElement.TryGetProperty("type", out JsonElement typeProp)) yield break;
        if (typeProp.GetString() != "RUN_STARTED") yield break;

        string msgId = Guid.NewGuid().ToString("N");
        yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_START", messageId = msgId, role = "assistant" });
        yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_CONTENT", messageId = msgId, delta = "[injected-by-middleware]" });
        yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_END", messageId = msgId });
    }
}

// ── Test class ────────────────────────────────────────────────────────────────

public sealed class SseMiddlewareEventInjectionTests : IAsyncDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    [Fact]
    public async Task MiddlewareInjectsTextEventAfterRunStartedAsync()
    {
        // Arrange
        await SetupTestServerAsync();

        var chatClient = new AGUIChatClient(this._client!, "", null);
        AIAgent agent = chatClient.AsAIAgent(
            instructions: null, name: "assistant", description: "test", tools: []);
        ChatClientAgentSession session =
            (ChatClientAgentSession)await agent.CreateSessionAsync();

        List<AgentResponseUpdate> updates = [];

        // Act
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, "hello")], session, new AgentRunOptions()))
        {
            updates.Add(update);
        }

        // Assert — original agent response is present
        string allText = string.Concat(
            updates.ConvertAll(u => u.Text ?? string.Empty));

        allText.Should().Contain("Hello from fake agent!",
            "the real agent response must still arrive");

        // Assert — middleware-injected event is present
        allText.Should().Contain("[injected-by-middleware]",
            "the middleware must have injected its text event after RUN_STARTED");
    }

    private async Task SetupTestServerAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddAGUI();
        builder.Services.AddSingleton<FakeChatClientAgent>();

        this._app = builder.Build();

        // Middleware MUST be registered before the endpoint mapping
        this._app.UseMiddleware<SseEventInjectionMiddleware>();

        FakeChatClientAgent agent =
            this._app.Services.GetRequiredService<FakeChatClientAgent>();
        this._app.MapAGUI("/agent", agent);

        await this._app.StartAsync();

        TestServer testServer =
            this._app.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer not found");

        this._client = testServer.CreateClient();
        this._client.BaseAddress = new Uri("http://localhost/agent");
    }

    public async ValueTask DisposeAsync()
    {
        this._client?.Dispose();
        if (this._app != null)
            await this._app.DisposeAsync();
    }
}
