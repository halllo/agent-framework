# Persisted Tool Approval Replay

This sample provides an interactive command-line chat experience backed by one persisted `AgentSession` and one persisted `ChatMessage` transcript. Each time you start the sample, it resumes the same saved conversation until you explicitly reset it.

## What this sample demonstrates

- Creating multiple approval-required function tools with `ApprovalRequiredAIFunction`
- Persisting `AgentSession` to disk with `SerializeSessionAsync`
- Persisting the raw `List<ChatMessage>` transcript to disk
- Resuming the same conversation across multiple process runs
- Interactively approving or denying tool calls in the terminal

## Prerequisites

- .NET 10 SDK or later
- OpenAI API key configured through environment variables or .NET user secrets

Set the following environment variable:

```powershell
$env:OPENAI_API_KEY="your-openai-api-key"
```

Or use the project's existing user secrets ID:

```powershell
cd dotnet/samples/02-agents/Agents/Agent_Step22_PersistedToolApprovalReplay
dotnet user-secrets set "OPENAI_API_KEY" "your-openai-api-key"
dotnet user-secrets set "OPENAI_CHAT_MODEL_NAME" "gpt-5.4-mini"
```

## Run the sample

```powershell
cd dotnet/samples/02-agents/Agents
dotnet run --project .\Agent_Step22_PersistedToolApprovalReplay
```

Ask for weather or local time information and the agent can request one of these approval-gated tools:

- `GetWeather`
- `GetLocalTime`

When a tool call is requested, the sample pauses and asks whether you want to approve it.

The sample supports these interactive commands:

- `/history` to print the persisted transcript
- `/reset` to delete the persisted state and start a new single session
- `/exit` to quit while keeping the current session on disk

The persisted state is stored under the sample output directory in `persisted-chat-state`.

## Notes

- The sample persists only one conversation. Restarting the process continues that same session.
- Tool approvals are interactive and must be answered in the terminal before the conversation continues.