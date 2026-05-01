// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.IntegrationTests;

// ── Minimal replicated input model ───────────────────────────────────────────
// Mirrors the internal RunAgentInput / AGUIMessage hierarchy using only public
// types.  JsonElement[] avoids the need for a polymorphic message converter.

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by ASP.NET Core JSON model binding")]
internal sealed record CustomRunInput(
    [property: JsonPropertyName("threadId")] string? ThreadId,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("messages")] JsonElement[]? Messages
);

// ── Minimal replicated event converter ───────────────────────────────────────
// Turns an AgentResponseUpdate stream into AG-UI SSE JSON strings using only
// public Microsoft.Extensions.AI types and System.Text.Json.
// The injection point is a plain yield statement — no framework hooks needed.

internal static class AguiSseConverter
{
    public static async IAsyncEnumerable<string> ToAguiEventsAsync(
        IAsyncEnumerable<AgentResponseUpdate> updates,
        string threadId,
        string runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return JsonSerializer.Serialize(new { type = "RUN_STARTED", threadId, runId });

        string? currentMessageId = null;
        // Fallback ID used when the upstream agent omits MessageId on updates
        string fallbackMsgId = Guid.NewGuid().ToString("N");

        await foreach (AgentResponseUpdate update in updates.WithCancellation(cancellationToken))
        {
            string msgId = string.IsNullOrWhiteSpace(update.MessageId)
                ? fallbackMsgId
                : update.MessageId;

            foreach (AIContent content in update.Contents ?? [])
            {
                if (content is not TextContent text || string.IsNullOrEmpty(text.Text))
                    continue;

                // Open a new message block when the message ID changes
                if (!string.Equals(currentMessageId, msgId, StringComparison.Ordinal))
                {
                    if (currentMessageId is not null)
                        yield return JsonSerializer.Serialize(
                            new { type = "TEXT_MESSAGE_END", messageId = currentMessageId });

                    yield return JsonSerializer.Serialize(
                        new { type = "TEXT_MESSAGE_START", messageId = msgId,
                              role = update.Role?.Value ?? "assistant" });

                    currentMessageId = msgId;
                }

                yield return JsonSerializer.Serialize(
                    new { type = "TEXT_MESSAGE_CONTENT", messageId = msgId, delta = text.Text });
            }
        }

        // Close the last open message block
        if (currentMessageId is not null)
            yield return JsonSerializer.Serialize(
                new { type = "TEXT_MESSAGE_END", messageId = currentMessageId });

        // ── INJECTION POINT ──────────────────────────────────────────────────
        // Add any extra events here.  Because we control the async iterator we
        // can yield events at any position in the stream.
        string injectedId = Guid.NewGuid().ToString("N");
        yield return JsonSerializer.Serialize(
            new { type = "TEXT_MESSAGE_START", messageId = injectedId, role = "assistant" });
        yield return JsonSerializer.Serialize(
            new { type = "TEXT_MESSAGE_CONTENT", messageId = injectedId,
                  delta = "[injected-by-custom-endpoint]" });
        yield return JsonSerializer.Serialize(
            new { type = "TEXT_MESSAGE_END", messageId = injectedId });
        // ────────────────────────────────────────────────────────────────────

        yield return JsonSerializer.Serialize(new { type = "RUN_FINISHED", threadId, runId });
    }

    /// <summary>
    /// Minimal AG-UI → <see cref="ChatMessage"/> conversion.
    /// Only handles text-bearing messages; enough for the test scenario.
    /// </summary>
    public static IEnumerable<ChatMessage> ParseMessages(JsonElement[]? messages)
    {
        if (messages is null) yield break;

        foreach (JsonElement msg in messages)
        {
            string? role = msg.TryGetProperty("role", out JsonElement r) ? r.GetString() : null;

            ChatRole chatRole = role switch
            {
                "assistant" => ChatRole.Assistant,
                "system"    => ChatRole.System,
                "tool"      => ChatRole.Tool,
                _           => ChatRole.User,
            };

            string content = msg.TryGetProperty("content", out JsonElement c)
                ? c.GetString() ?? string.Empty
                : string.Empty;

            yield return new ChatMessage(chatRole, content);
        }
    }
}

// ── Test class ────────────────────────────────────────────────────────────────

public sealed class CustomAguiEndpointTests : IAsyncDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    [Fact]
    public async Task CustomEndpointInjectsTextEventBeforeRunFinishedAsync()
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

        // Assert — custom-endpoint-injected event is present
        allText.Should().Contain("[injected-by-custom-endpoint]",
            "the custom endpoint must have injected its extra text event");
    }

    private async Task SetupTestServerAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // No AddAGUI() needed — the custom endpoint bypasses the AGUI hosting layer
        builder.Services.AddSingleton<FakeChatClientAgent>();

        this._app = builder.Build();

        FakeChatClientAgent backendAgent =
            this._app.Services.GetRequiredService<FakeChatClientAgent>();

        // Mirrors MapAGUI: resolve a keyed session store or fall back to the no-op store.
        var agentSessionStore =
            this._app.Services.GetKeyedService<AgentSessionStore>(backendAgent.Name)
            ?? new NoopAgentSessionStore();
        var hostAgent = new AIHostAgent(backendAgent, agentSessionStore);

        // Custom endpoint — the minimal replication of MapAGUI with an injection point
        this._app.MapPost("/custom-agent", async (HttpContext context, CancellationToken ct) =>
        {
            // Replicates the core MapAGUI endpoint logic (AGUIEndpointRouteBuilderExtensions), but with direct access to the HttpContext
            CustomRunInput input =
                await context.Request.ReadFromJsonAsync<CustomRunInput>(ct)
                ?? throw new InvalidOperationException("Empty request body");

            string threadId = string.IsNullOrWhiteSpace(input.ThreadId)
                ? Guid.NewGuid().ToString("N")
                : input.ThreadId;

            IEnumerable<ChatMessage> messages =
                AguiSseConverter.ParseMessages(input.Messages);

            AgentSession session = await hostAgent.GetOrCreateSessionAsync(threadId, ct);

            IAsyncEnumerable<AgentResponseUpdate> updates =
                hostAgent.RunStreamingAsync(messages, session, cancellationToken: ct);

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache,no-store";
            context.Response.Headers.Pragma = "no-cache";

            await foreach (string eventJson in AguiSseConverter.ToAguiEventsAsync(
                updates, threadId, input.RunId, ct))
            {
                byte[] bytes = Encoding.UTF8.GetBytes($"data: {eventJson}\n\n");
                await context.Response.Body.WriteAsync(bytes, ct);
                await context.Response.Body.FlushAsync(ct);
            }

            await hostAgent.SaveSessionAsync(threadId, session, ct);
        });

        await this._app.StartAsync();

        TestServer testServer =
            this._app.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer not found");

        this._client = testServer.CreateClient();
        this._client.BaseAddress = new Uri("http://localhost/custom-agent");
    }

    public async ValueTask DisposeAsync()
    {
        this._client?.Dispose();
        if (this._app != null)
            await this._app.DisposeAsync();
    }
}
