# Persisted Tool Approval Replay

This sample persists both the `AgentSession` and the full `ChatMessage` transcript to disk, then replays that stored transcript across multiple process runs. It is intended as a small repro harness for investigating replay issues around completed tool approvals.

## What this sample demonstrates

- Creating an approval-required function tool with `ApprovalRequiredAIFunction`
- Persisting `AgentSession` to disk with `SerializeSessionAsync`
- Persisting the raw `List<ChatMessage>` transcript to disk
- Replaying a completed approval transcript on a later follow-up turn

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
dotnet run --project .\Agent_Step22_PersistedToolApprovalReplay -- request
dotnet run --project .\Agent_Step22_PersistedToolApprovalReplay -- approve
dotnet run --project .\Agent_Step22_PersistedToolApprovalReplay -- followup
```

Use `clear` to delete the persisted repro state:

```powershell
dotnet run --project .\Agent_Step22_PersistedToolApprovalReplay -- clear
```

The sample intentionally does not normalize the stored history. It is meant to expose the raw replay behavior first.