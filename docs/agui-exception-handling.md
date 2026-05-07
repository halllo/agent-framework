# AG-UI Exception Handling in the ASP.NET Core Host

## Background

When an AGUI agent endpoint receives a request, the route handler in
`AGUIEndpointRouteBuilderExtensions.MapAGUI` does two kinds of work:

1. **Eager work** — runs synchronously/awaited before returning the HTTP result.
2. **Deferred (streaming) work** — runs lazily inside `AGUIServerSentEventsResult.ExecuteAsync`
   after the handler has already returned the result object.

These two phases have completely different exception handling behaviour.

---

## Current Behaviour

### Streaming exceptions → `RUN_ERROR` SSE event (already handled)

All exceptions that originate inside the `IAsyncEnumerable` pipeline
(`RunStreamingAsync`, `AsChatResponseUpdatesAsync`, `AsAGUIEventStreamAsync`,
`SaveSessionAfterStreamingAsync`) are caught by
`AGUIServerSentEventsResult.ExecuteAsync` (lines ~51–73):

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // logs the error, then emits:
    new RunErrorEvent { Code = "StreamingError", Message = ex.Message }
}
```

The client receives a proper `{ "type": "RUN_ERROR", ... }` SSE event.
`OperationCanceledException` is intentionally not caught — the stream just ends.

### Eager exceptions → **500 Internal Server Error** (the gap)

The following lines in the route handler lambda execute *before*
`AGUIServerSentEventsResult` is even constructed:

| Line | Code | Throws → |
|------|------|----------|
| 98 | `input.Messages.AsChatMessages(jsonSerializerOptions)` | 500 |
| 99 | `input.Tools?.AsAITools().ToList()` | 500 |
| **119** | **`await hostAgent.GetOrCreateSessionAsync(threadId, ct)`** | **500** |

Because these throw inside the `async` route handler delegate (not inside the
SSE stream), ASP.NET Core's normal exception pipeline handles them — returning
a 500, **not** a `RUN_ERROR` SSE event.

`RunStreamingAsync` itself (line 122) is safe: it only *builds* an
`IAsyncEnumerable` pipeline and never executes until the SSE result iterates
it. It will not throw eagerly.

---

## What to Implement

Wrap the eager session creation (and optionally the other eager calls) in a
`try/catch` inside the route handler lambda. On failure, return an
`AGUIServerSentEventsResult` backed by a hand-built error event stream instead
of letting the exception escape as a 500.

### File to modify

```
dotnet/src/Microsoft.Agents.AI.Hosting.AGUI.AspNetCore/AGUIEndpointRouteBuilderExtensions.cs
```

All required types (`RunStartedEvent`, `RunErrorEvent`, `BaseEvent`) are already
available in this file's compilation unit — the `.csproj` links all files from
`Microsoft.Agents.AI.AGUI/Shared/` with `ASPNETCORE` defined, placing them in
`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared`, which is already imported.

### Change 1 — wrap `GetOrCreateSessionAsync` in try/catch

Replace the bare call on line 119:

```csharp
// before
var session = await hostAgent.GetOrCreateSessionAsync(threadId, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
// after
AgentSession session;
try
{
    session = await hostAgent.GetOrCreateSessionAsync(threadId, cancellationToken).ConfigureAwait(false);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    var sseLogger = context.RequestServices.GetRequiredService<ILogger<AGUIServerSentEventsResult>>();
    return new AGUIServerSentEventsResult(
        SingleRunErrorAsync(threadId, input.RunId, "SessionError", ex.Message),
        sseLogger);
}
```

### Change 2 — add the helper method

Add a private static helper to `AGUIEndpointRouteBuilderExtensions` alongside
the existing `SaveSessionAfterStreamingAsync`:

```csharp
private static async IAsyncEnumerable<BaseEvent> SingleRunErrorAsync(
    string threadId,
    string? runId,
    string code,
    string message,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    yield return new RunStartedEvent { ThreadId = threadId, RunId = runId ?? string.Empty };
    yield return new RunErrorEvent { Code = code, Message = message };
}
```

`RunStartedEvent` is emitted first because AG-UI clients expect the run
lifecycle to begin before any error is reported (matching what
`AsAGUIEventStreamAsync` does in the normal path).

---

## Why only `GetOrCreateSessionAsync`?

The other two eager calls (`AsChatMessages`, `AsAITools`) are pure in-memory
transformations of the request body. They can only throw if the request is
malformed, which is an HTTP-level problem correctly signalled by a 400/500.

`GetOrCreateSessionAsync` may call an external session store (e.g. database,
Redis) and is the realistic source of infrastructure failures that the AG-UI
client should handle gracefully via `RUN_ERROR`.

If you also want `AsChatMessages` failures to surface as `RUN_ERROR`, move
lines 98–99 inside the same try/catch block (or a separate one with
`code = "InputError"`).

---

## Verification

1. Register a failing `AgentSessionStore` that throws on `GetOrCreateSessionAsync`.
2. POST a valid AG-UI request to the mapped endpoint.
3. Assert the response is `200 OK` with `Content-Type: text/event-stream`.
4. Assert the SSE stream contains a `RUN_STARTED` event followed by a
   `RUN_ERROR` event with `"code": "SessionError"` and the exception message.
5. Assert no `500` status is returned.

Existing integration tests live in
`dotnet/tests/Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.IntegrationTests/`.
