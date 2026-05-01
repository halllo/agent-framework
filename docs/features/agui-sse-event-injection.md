# AG-UI SSE Event Injection — Two Consumer Approaches

## Context

`MapAGUI`, `AGUIServerSentEventsResult`, and `BaseEvent` are all `internal sealed` — no public extension points exist for consumers who want to inject additional AG-UI events into the SSE stream. Two consumer-side approaches are available that work without modifying the framework.

Both approaches are covered by integration tests in
`dotnet/tests/Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.IntegrationTests/`.

---

## Option 1 — HTTP Pipeline Middleware

**File:** `SseMiddlewareEventInjectionTests.cs`

### How it works

Swap `HttpContext.Response.Body` with a custom `Stream` wrapper before the endpoint runs. The wrapper buffers incoming bytes, detects SSE event boundaries (`\n\n`), forwards each complete event to the real stream, and calls a user-supplied injector for each event to optionally emit additional events inline.

### Key types

- **`SseInterceptorStream : Stream`** — write-only stream that accumulates bytes in a `MemoryStream`, splits on `\n\n`, forwards original events, and inserts injected events immediately after each matched event.
- **`SseEventInjectionMiddleware`** — ASP.NET Core middleware; replaces `Response.Body` before calling `next()` and restores it in `finally`.

### Registration

Registration order matters — the middleware must be added **before** `MapAGUI`:

```csharp
app.UseMiddleware<SseEventInjectionMiddleware>(); // must come first
app.MapAGUI("/agent", agent);
```

### Injection callback signature

```csharp
Func<string, IEnumerable<string>> injector
```

The injector receives each event's raw JSON payload (without the `data: ` prefix) and returns zero or more JSON strings to emit as additional SSE events immediately after it.

### Example — inject after `RUN_STARTED`

```csharp
private static IEnumerable<string> InjectAfter(string eventJson)
{
    using JsonDocument doc = JsonDocument.Parse(eventJson);
    if (!doc.RootElement.TryGetProperty("type", out JsonElement typeProp)) yield break;
    if (typeProp.GetString() != "RUN_STARTED") yield break;

    string msgId = Guid.NewGuid().ToString("N");
    yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_START", messageId = msgId, role = "assistant" });
    yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_CONTENT", messageId = msgId, delta = "[injected]" });
    yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_END", messageId = msgId });
}
```

### Trade-offs

| | |
|---|---|
| **Works with** | Unmodified `MapAGUI` — framework features (session store, tool filtering, error recovery) are preserved |
| **Operates at** | Raw SSE byte level — injector receives and returns JSON strings, not typed objects |
| **Complexity** | Must handle SSE framing; relies on `\n\n` boundary detection being correct |
| **Good for** | Lightweight cross-cutting injection (audit events, observability markers, A/B flags) |

---

## Option 2 — Minimal Endpoint Replication

**File:** `CustomAguiEndpointTests.cs`

### How it works

Bypass `MapAGUI` entirely. Register a plain `MapPost` endpoint that calls the agent directly, drives the `AgentResponseUpdate` stream, and produces SSE output using `JsonSerializer` and anonymous objects. Because you own the `async` iterator you can `yield return` events at any position in the stream.

### Key types

- **`CustomRunInput`** — input record matching the AG-UI wire format (`threadId`, `runId`, `messages`).
- **`AguiSseConverter`** — static helper with:
  - `ToAguiEventsAsync` — converts an `IAsyncEnumerable<AgentResponseUpdate>` to AG-UI SSE JSON strings with a built-in injection point before `RUN_FINISHED`.
  - `ParseMessages` — converts AG-UI JSON message array to `IEnumerable<ChatMessage>`.

### Injection point

```csharp
// After all agent updates, before RUN_FINISHED:
string injectedId = Guid.NewGuid().ToString("N");
yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_START", messageId = injectedId, role = "assistant" });
yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_CONTENT", messageId = injectedId, delta = "[injected]" });
yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_END", messageId = injectedId });

yield return JsonSerializer.Serialize(new { type = "RUN_FINISHED", threadId, runId });
```

### Session handling

The endpoint mirrors `MapAGUI`'s session lifecycle exactly:

1. **Resolve the session store** — look up a keyed `AgentSessionStore` by agent name from DI; fall back to `NoopAgentSessionStore` (same fallback `MapAGUI` uses).
2. **Wrap the agent** — `AIHostAgent` adds `GetOrCreateSessionAsync` / `SaveSessionAsync` on top of the base agent.
3. **Get or create** per thread ID — restores an existing session when a store is wired up.
4. **Save after streaming** — called once the event loop finishes, persisting any state accumulated during the run.

### Registration

```csharp
// No AddAGUI() or MapAGUI() needed.
// Wrap the agent once at startup — mirrors the two lines inside MapAGUI.
var agentSessionStore =
    app.Services.GetKeyedService<AgentSessionStore>(myAgent.Name)
    ?? new NoopAgentSessionStore();
var hostAgent = new AIHostAgent(myAgent, agentSessionStore);

app.MapPost("/custom-agent", async (HttpContext context, CancellationToken ct) =>
{
    var input = await context.Request.ReadFromJsonAsync<CustomRunInput>(ct);

    string threadId = string.IsNullOrWhiteSpace(input.ThreadId)
        ? Guid.NewGuid().ToString("N") : input.ThreadId;

    var messages = AguiSseConverter.ParseMessages(input.Messages);

    // Get or restore session for this thread
    AgentSession session = await hostAgent.GetOrCreateSessionAsync(threadId, ct);
    var updates = hostAgent.RunStreamingAsync(messages, session, cancellationToken: ct);

    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache,no-store";

    await foreach (string eventJson in AguiSseConverter.ToAguiEventsAsync(updates, threadId, input.RunId, ct))
    {
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"data: {eventJson}\n\n"), ct);
        await context.Response.Body.FlushAsync(ct);
    }

    // Persist the session (no-op when no store is registered)
    await hostAgent.SaveSessionAsync(threadId, session, ct);
});
```

### Trade-offs

| | |
|---|---|
| **Full control** | Events can be injected, reordered, or suppressed anywhere in the stream |
| **Session handling** | Equivalent to `MapAGUI` via `AIHostAgent` + `AgentSessionStore` |
| **Tool filtering / error recovery** | Must be re-implemented manually if needed |
| **Type safety** | `BaseEvent` subclasses are `internal` — AG-UI event objects must be anonymous types or custom records serialized to the correct JSON shape |
| **Good for** | Scenarios requiring deep customisation of the event stream or integration with agents that don't fit the standard pipeline |

---

## Choosing an approach

| Criterion | Option 1 (Middleware) | Option 2 (Custom endpoint) |
|---|---|---|
| Framework session persistence | Preserved | Equivalent (`AIHostAgent` + `AgentSessionStore`) |
| Tool call filtering | Preserved | Must re-implement |
| Injection granularity | Per-event callback | Anywhere in the async iterator |
| Code surface | Small (one `Stream` subclass + one middleware) | Larger (converter + endpoint handler) |
| AG-UI type safety | None (raw JSON strings) | None (`BaseEvent` is internal) |
| Maintenance risk | Low — tolerates framework internal changes | Medium — replicates SSE formatting |
