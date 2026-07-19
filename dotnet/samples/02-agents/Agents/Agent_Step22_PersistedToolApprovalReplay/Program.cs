// Copyright (c) Microsoft. All rights reserved.

// Persisted Tool Approval Replay — Reproduce approval-history replay issues
//
// This sample persists both the AgentSession and the full ChatMessage transcript
// to disk, then replays that stored transcript across multiple process runs.
// It is intended as a small repro harness for investigating how completed tool
// approvals interact with later conversation replay.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

IConfigurationRoot configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>()
    .Build();

var openaiApiKey = configuration["OPENAI_API_KEY"] ?? throw new InvalidOperationException("OPENAI_API_KEY is not set.");
var model = configuration["OPENAI_CHAT_MODEL_NAME"] ?? "gpt-5.4-mini";
var command = args.FirstOrDefault()?.ToUpperInvariant() ?? "REQUEST";
var followUpText = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "What else can you tell me about Amsterdam today?";
var stateDirectory = Path.Combine(AppContext.BaseDirectory, "approval-repro-state");
var sessionPath = Path.Combine(stateDirectory, "session.json");
var historyPath = Path.Combine(stateDirectory, "history.json");
var jsonOptions = new JsonSerializerOptions(AgentAbstractionsJsonUtilities.DefaultOptions)
{
    WriteIndented = true,
};

[Description("Get the weather for a given location.")]
static string GetWeather([Description("The location to get the weather for.")] string location)
    => $"The weather in {location} is cloudy with a high of 15°C.";

AIAgent agent = new OpenAIClient(openaiApiKey)
    .GetChatClient(model)
    .AsIChatClient()
    .AsAIAgent(
        instructions: "You are a helpful assistant",
        tools: [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(GetWeather))]);

switch (command)
{
    case "REQUEST":
        Directory.CreateDirectory(stateDirectory);

        AgentSession freshSession = await agent.CreateSessionAsync();
        List<ChatMessage> requestMessages = [new(ChatRole.User, "What is the weather like in Amsterdam?")];
        AgentResponse initialResponse = await agent.RunAsync(requestMessages, freshSession);
        List<ChatMessage> initialHistory = [.. requestMessages, .. initialResponse.Messages];

        await SaveStateAsync(freshSession, initialHistory);

        Console.WriteLine($"Saved initial approval request to '{stateDirectory}'.");
        PrintHistory(initialHistory);
        Console.WriteLine("\nNext step: run with 'approve' to resume the conversation from disk.");
        break;

    case "APPROVE":
        EnsureStateExists();

        AgentSession approvalSession = await LoadSessionAsync();
        List<ChatMessage> approvalHistory = await LoadHistoryAsync();
        ToolApprovalRequestContent pendingApproval = approvalHistory
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>()
            .LastOrDefault()
            ?? throw new InvalidOperationException("No pending ToolApprovalRequestContent found in persisted history.");

        ChatMessage approvalMessage = new(
            ChatRole.User,
            [pendingApproval.CreateResponse(approved: true, reason: "Approved from persisted repro harness")]);

        List<ChatMessage> approvalRequestMessages = [.. approvalHistory, approvalMessage];
        AgentResponse approvedResponse = await agent.RunAsync(approvalRequestMessages, approvalSession);
        List<ChatMessage> approvedHistory = [.. approvalRequestMessages, .. approvedResponse.Messages];

        await SaveStateAsync(approvalSession, approvedHistory);

        Console.WriteLine($"Saved approved conversation to '{stateDirectory}'.");
        PrintHistory(approvedHistory);
        Console.WriteLine("\nNext step: run with 'followup' to replay the completed approval history.");
        break;

    case "FOLLOWUP":
        EnsureStateExists();

        AgentSession resumedSession = await LoadSessionAsync();
        List<ChatMessage> resumedHistory = await LoadHistoryAsync();
        ChatMessage followUpMessage = new(ChatRole.User, followUpText);
        List<ChatMessage> followUpRequestMessages = [.. resumedHistory, followUpMessage];

        Console.WriteLine("Replaying persisted history plus a new user message...");

        try
        {
            AgentResponse followUpResponse = await agent.RunAsync(followUpRequestMessages, resumedSession);
            List<ChatMessage> finalHistory = [.. followUpRequestMessages, .. followUpResponse.Messages];
            await SaveStateAsync(resumedSession, finalHistory);

            Console.WriteLine("Follow-up completed without throwing.");
            PrintHistory(finalHistory);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Follow-up threw an exception while replaying the persisted approval history:");
            Console.WriteLine(ex);
            System.Environment.ExitCode = 1;
        }
        break;

    case "CLEAR":
        if (Directory.Exists(stateDirectory))
        {
            Directory.Delete(stateDirectory, recursive: true);
        }

        Console.WriteLine($"Deleted '{stateDirectory}'.");
        break;

    default:
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- request");
        Console.WriteLine("  dotnet run -- approve");
        Console.WriteLine("  dotnet run -- followup [message]");
        Console.WriteLine("  dotnet run -- clear");
        System.Environment.ExitCode = 2;
        break;
}

return;

void EnsureStateExists()
{
    if (!File.Exists(sessionPath) || !File.Exists(historyPath))
    {
        throw new InvalidOperationException($"No persisted state found in '{stateDirectory}'. Run the sample with 'request' first.");
    }
}

async Task SaveStateAsync(AgentSession session, List<ChatMessage> history)
{
    Directory.CreateDirectory(stateDirectory);

    JsonElement serializedSession = await agent.SerializeSessionAsync(session);
    await File.WriteAllTextAsync(sessionPath, JsonSerializer.Serialize(serializedSession, jsonOptions));
    await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(history, jsonOptions));
}

async Task<AgentSession> LoadSessionAsync()
{
    await using FileStream sessionStream = File.OpenRead(sessionPath);
    JsonElement sessionState = await JsonSerializer.DeserializeAsync<JsonElement>(sessionStream, jsonOptions);
    return await agent.DeserializeSessionAsync(sessionState);
}

async Task<List<ChatMessage>> LoadHistoryAsync()
{
    await using FileStream historyStream = File.OpenRead(historyPath);
    return await JsonSerializer.DeserializeAsync<List<ChatMessage>>(historyStream, jsonOptions)
        ?? throw new InvalidOperationException("Persisted history could not be deserialized.");
}

void PrintHistory(List<ChatMessage> history)
{
    Console.WriteLine("\nPersisted history:");

    for (var i = 0; i < history.Count; i++)
    {
        ChatMessage message = history[i];
        string summary = string.Join(", ",
            message.Contents.Select(content => content switch
            {
                TextContent text => $"Text('{text.Text}')",
                ToolApprovalRequestContent request => $"ToolApprovalRequest({((FunctionCallContent)request.ToolCall).Name}, requestId={request.RequestId})",
                ToolApprovalResponseContent response => $"ToolApprovalResponse(approved={response.Approved}, requestId={response.RequestId}, informationalOnly={((FunctionCallContent)response.ToolCall).InformationalOnly})",
                FunctionCallContent call => $"FunctionCall({call.Name}, callId={call.CallId}, informationalOnly={call.InformationalOnly})",
                FunctionResultContent result => $"FunctionResult(callId={result.CallId})",
                _ => content.GetType().Name,
            }));

        Console.WriteLine($"[{i}] {message.Role}: {summary}");
    }
}