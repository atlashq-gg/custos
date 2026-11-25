using System.Text.Json;
using Optio.Harness;

namespace Optio.Queries;

/// <summary>
/// The conversation of a client: the real messages of its earlier queries, the oldest first. Each
/// exchange holds the question, the tool calls with their results, and the answer. A tool result
/// stays a tool result, because it holds text from players.
/// </summary>
internal sealed record Conversation(List<Exchange> Exchanges)
{
    public IEnumerable<ModelMessage> Messages() =>
        Exchanges.SelectMany(e => e.Messages.Select(m => m.ToModel()));

    /// <summary>The conversation with <paramref name="exchange"/> at the end, as JSON of at most
    /// <paramref name="maxBytes"/>. It removes the oldest exchanges to fit. Null when the new
    /// exchange alone is too large.</summary>
    public static byte[]? Append(Conversation? conversation, Exchange exchange, int maxBytes)
    {
        List<Exchange> exchanges = [.. conversation?.Exchanges ?? [], exchange];
        while (exchanges.Count > 0)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(
                new Conversation(exchanges),
                QueryMessage.Json
            );
            if (json.Length <= maxBytes)
                return json;
            exchanges.RemoveAt(0);
        }
        return null;
    }
}

/// <summary>The messages of one query.</summary>
internal sealed record Exchange(List<StoredMessage> Messages)
{
    /// <summary>The exchange of the messages of a query. The thinking of the provider stays
    /// out.</summary>
    public static Exchange Of(IEnumerable<ModelMessage> messages) =>
        new([.. messages.Select(StoredMessage.Of)]);
}

/// <summary>A message of the conversation with no content of the provider: a question
/// (<c>user</c>), a turn of the model (<c>model</c>), or tool results (<c>results</c>).</summary>
internal sealed record StoredMessage(
    string Role,
    string? Text = null,
    List<ToolCall>? Calls = null,
    List<ToolResult>? Results = null
)
{
    public static StoredMessage Of(ModelMessage message) =>
        message switch
        {
            UserMessage user => new("user", user.Text),
            AssistantMessage model => new("model", model.Turn.Text, [.. model.Turn.ToolCalls]),
            ToolResultsMessage results => new("results", Results: [.. results.Results]),
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };

    public ModelMessage ToModel() =>
        Role switch
        {
            "user" => new UserMessage(Text ?? ""),
            "model" => new AssistantMessage(
                new ModelTurn(
                    Text ?? "",
                    Calls ?? [],
                    Calls is { Count: > 0 } ? ModelStop.ToolCalls : ModelStop.End
                )
            ),
            _ => new ToolResultsMessage(Results ?? []),
        };
}
