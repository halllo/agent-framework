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
var stateDirectory = Path.Combine(AppContext.BaseDirectory, "persisted-chat-state");
var sessionPath = Path.Combine(stateDirectory, "session.json");
var historyPath = Path.Combine(stateDirectory, "history.json");
var jsonOptions = new JsonSerializerOptions(AgentAbstractionsJsonUtilities.DefaultOptions)
{
    WriteIndented = true,
};

[Description("Get the weather for a given location.")]
static string GetWeather([Description("The location to get the weather for.")] string location)
    => $"The weather in {location} is cloudy with a high of 15°C.";

[Description("Get the local time for a given location.")]
static string GetLocalTime([Description("The location to get the local time for.")] string location)
    => $"The current local time in {location} is {DateTimeOffset.UtcNow:HH:mm} UTC.";

AIAgent agent = new OpenAIClient(openaiApiKey)
    .GetChatClient(model)
    .AsIChatClient()
    .AsAIAgent(
        instructions: "You are a helpful assistant. Use the available tools when the user asks for weather or local time information.",
        tools:
        [
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(GetWeather, name: nameof(GetWeather))),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(GetLocalTime, name: nameof(GetLocalTime))),
        ]);

PersistedConversation persistedConversation = await LoadOrCreateConversationAsync();

Console.WriteLine("Persisted Tool Approval Chat");
Console.WriteLine($"State directory: {stateDirectory}");
Console.WriteLine("Type a message to continue the persisted session.");
Console.WriteLine("Commands: /exit, /reset, /history");

if (persistedConversation.History.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Resumed persisted conversation:");
    PrintHistory(persistedConversation.History);

    List<ToolApprovalRequestContent> pendingApprovalRequests = GetPendingApprovalRequests(persistedConversation.History);
    if (pendingApprovalRequests.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Resuming pending tool approval request.");
        await ContinueApprovalRequestsAsync(pendingApprovalRequests);
    }
}
else
{
    Console.WriteLine();
    Console.WriteLine("Starting a new persisted conversation.");
}

while (true)
{
    Console.WriteLine();
    Console.Write("You: ");
    string? input = Console.ReadLine();

    if (input is null)
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (input.Equals("/history", StringComparison.OrdinalIgnoreCase))
    {
        PrintHistory(persistedConversation.History);
        continue;
    }

    if (input.Equals("/reset", StringComparison.OrdinalIgnoreCase))
    {
        persistedConversation = await ResetConversationAsync();
        Console.WriteLine("Started a new persisted conversation.");
        continue;
    }

    ChatMessage userMessage = new(ChatRole.User, input);
    persistedConversation.History.Add(userMessage);
    await SaveStateAsync(persistedConversation.Session, persistedConversation.History);

    await RunTurnAsync([userMessage]);
}

return;

async Task RunTurnAsync(List<ChatMessage> inputMessages)
{
    AgentResponse response = await agent.RunAsync(inputMessages, persistedConversation.Session);
    persistedConversation.History.AddRange(response.Messages);
    await SaveStateAsync(persistedConversation.Session, persistedConversation.History);

    PrintAgentOutput(response.Messages);

    List<ToolApprovalRequestContent> approvalRequests = response.Messages
        .SelectMany(message => message.Contents)
        .OfType<ToolApprovalRequestContent>()
        .ToList();

    await ContinueApprovalRequestsAsync(approvalRequests);
}

async Task ContinueApprovalRequestsAsync(List<ToolApprovalRequestContent> approvalRequests)
{
    while (approvalRequests.Count > 0)
    {
        List<ChatMessage> approvalResponses = approvalRequests
            .ConvertAll(CreateApprovalResponseMessage);

        persistedConversation.History.AddRange(approvalResponses);
        await SaveStateAsync(persistedConversation.Session, persistedConversation.History);

        AgentResponse response = await agent.RunAsync(approvalResponses, persistedConversation.Session);
        persistedConversation.History.AddRange(response.Messages);
        await SaveStateAsync(persistedConversation.Session, persistedConversation.History);

        PrintAgentOutput(response.Messages);

        approvalRequests = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>()
            .ToList();
    }
}

List<ToolApprovalRequestContent> GetPendingApprovalRequests(List<ChatMessage> history)
{
    if (history.Count == 0)
    {
        return [];
    }

    ChatMessage lastMessage = history[^1];
    return lastMessage.Contents
        .OfType<ToolApprovalRequestContent>()
        .ToList();
}

async Task SaveStateAsync(AgentSession session, List<ChatMessage> history)
{
    Directory.CreateDirectory(stateDirectory);

    JsonElement serializedSession = await agent.SerializeSessionAsync(session);
    await File.WriteAllTextAsync(sessionPath, JsonSerializer.Serialize(serializedSession, jsonOptions));
    await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(history, jsonOptions));
}

async Task<PersistedConversation> LoadOrCreateConversationAsync()
{
    if (!File.Exists(sessionPath) || !File.Exists(historyPath))
    {
        Directory.CreateDirectory(stateDirectory);
        AgentSession newSession = await agent.CreateSessionAsync();
        List<ChatMessage> emptyHistory = [];
        await SaveStateAsync(newSession, emptyHistory);
        return new PersistedConversation(newSession, emptyHistory);
    }

    return new PersistedConversation(await LoadSessionAsync(), await LoadHistoryAsync());
}

async Task<PersistedConversation> ResetConversationAsync()
{
    if (Directory.Exists(stateDirectory))
    {
        Directory.Delete(stateDirectory, recursive: true);
    }

    return await LoadOrCreateConversationAsync();
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

ChatMessage CreateApprovalResponseMessage(ToolApprovalRequestContent approvalRequest)
{
    if (approvalRequest.ToolCall is not FunctionCallContent functionCall)
    {
        throw new InvalidOperationException("Expected an approval request for a function call.");
    }

    Console.WriteLine();
    Console.WriteLine("Approval required:");
    Console.WriteLine($"  Tool: {functionCall.Name}");

    if (functionCall.Arguments is { Count: > 0 })
    {
        Console.WriteLine($"  Arguments: {JsonSerializer.Serialize(functionCall.Arguments)}");
    }

    while (true)
    {
        Console.Write("Approve? [y/N]: ");
        string? approvalInput = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(approvalInput))
        {
            return new ChatMessage(ChatRole.User, [approvalRequest.CreateResponse(approved: false, reason: "User denied the tool call.")]);
        }

        if (approvalInput.Equals("y", StringComparison.OrdinalIgnoreCase) || approvalInput.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatMessage(ChatRole.User, [approvalRequest.CreateResponse(approved: true, reason: "User approved the tool call.")]);
        }

        if (approvalInput.Equals("n", StringComparison.OrdinalIgnoreCase) || approvalInput.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatMessage(ChatRole.User, [approvalRequest.CreateResponse(approved: false, reason: "User denied the tool call.")]);
        }
    }
}

void PrintAgentOutput(IEnumerable<ChatMessage> messages)
{
    foreach (ChatMessage message in messages)
    {
        foreach (AIContent content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                    Console.WriteLine($"Agent: {textContent.Text}");
                    break;

                case FunctionCallContent functionCall:
                    Console.WriteLine($"[Tool Call] {functionCall.Name}");
                    break;

                case FunctionResultContent functionResult:
                    Console.WriteLine($"[Tool Result] {functionResult.Result}");
                    break;

                case ToolApprovalResponseContent approvalResponse:
                    Console.WriteLine($"[Approval {(approvalResponse.Approved ? "Approved" : "Denied")}] RequestId={approvalResponse.RequestId}");
                    break;

                case ErrorContent errorContent:
                    Console.WriteLine($"[Error] {errorContent.Message}");
                    break;
            }
        }
    }
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

sealed record PersistedConversation(AgentSession Session, List<ChatMessage> History);