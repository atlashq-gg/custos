using System.Text.Json;

namespace Optio.Harness;

/// <summary>
/// The model of the query harness. The query uses only these types, so that another provider
/// needs only one more implementation (decision on #48 of 2026-11-24).
/// </summary>
internal interface IModelClient
{
    /// <summary>The next turn of the model for the conversation of the request.</summary>
    Task<ModelTurn> NextAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>A request to the model. With <paramref name="Answer"/>, the model gets the tools but
/// cannot call one: it must answer.</summary>
internal sealed record ModelRequest(
    string System,
    IReadOnlyList<ModelTool> Tools,
    IReadOnlyList<ModelMessage> Messages,
    bool Answer = false
);

/// <summary>A tool for the model: its name, its description, and the JSON Schema of its
/// arguments. <paramref name="Status"/> is the text of the status line while the tool runs, for
/// example <c>Custos: Find items</c>.</summary>
internal sealed record ModelTool(
    string Name,
    string Status,
    string Description,
    JsonElement Schema
);

internal sealed record ToolCall(
    string Id,
    string Name,
    IReadOnlyDictionary<string, JsonElement> Arguments
);

internal sealed record ToolResult(string CallId, string Content, bool IsError);

internal enum ModelStop
{
    /// <summary>The model gave its answer.</summary>
    End,

    /// <summary>The model asks for the tool calls of the turn.</summary>
    ToolCalls,

    /// <summary>The model refused the request.</summary>
    Refusal,

    /// <summary>The turn reached a limit of the provider, for example the output tokens.</summary>
    Limit,
}

/// <summary>
/// One turn of the model. <paramref name="ProviderContent"/> is the content of the provider. The
/// harness gives it back with no change in the next request of the same query, because the
/// provider can need it (for example the thinking blocks of Claude). A turn of an earlier query of
/// the conversation has none: the client then makes the content from the text and the calls.
/// </summary>
internal sealed record ModelTurn(
    string Text,
    IReadOnlyList<ToolCall> ToolCalls,
    ModelStop Stop,
    object? ProviderContent = null
);

internal abstract record ModelMessage;

internal sealed record UserMessage(string Text) : ModelMessage;

internal sealed record AssistantMessage(ModelTurn Turn) : ModelMessage;

internal sealed record ToolResultsMessage(IReadOnlyList<ToolResult> Results) : ModelMessage;
