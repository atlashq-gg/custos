using System.Collections.Concurrent;
using System.Text.Json;
using Optio.Harness;

namespace Optio.Tests.Infrastructure;

/// <summary>
/// A model that gives the turns of <see cref="Turns"/> in order. With no turn left, it answers
/// <c>Done.</c>. <see cref="Gate"/> holds each request until the test releases it. The model keeps
/// each request.
/// </summary>
internal sealed class FakeModel : IModelClient
{
    public ConcurrentQueue<ModelTurn> Turns { get; } = new();

    public ConcurrentQueue<ModelRequest> Requests { get; } = new();

    /// <summary>When set, each request waits for it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Completes when a request comes.</summary>
    public TaskCompletionSource Called { get; private set; } = NewSource();

    public async Task<ModelTurn> NextAsync(
        ModelRequest request,
        CancellationToken cancellationToken
    )
    {
        // A copy: the query adds to its list of messages after the request.
        Requests.Enqueue(request with { Messages = [.. request.Messages] });
        Called.TrySetResult();
        if (Gate is { } gate)
            await gate.Task.WaitAsync(cancellationToken);
        return Turns.TryDequeue(out var turn) ? turn : Answer("Done.");
    }

    public void Reset() => Called = NewSource();

    public static ModelTurn Answer(string text) => new(text, [], ModelStop.End);

    public static ModelTurn Call(string tool, string arguments = "{}") =>
        new(
            "",
            [
                new ToolCall(
                    $"call-{Guid.NewGuid():N}",
                    tool,
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)!
                ),
            ],
            ModelStop.ToolCalls
        );

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// One service <c>custos</c> with the read tool <c>custos_find_items</c> and the write tool
/// <c>custos_set_structure_label</c>. A read call gives <see cref="Result"/>. A write call waits
/// for <see cref="WriteGate"/>, then gives <see cref="WriteResult"/>, or throws with
/// <see cref="WriteFails"/>. With <see cref="Available"/> false, the service has no tools.
/// </summary>
internal sealed class FakeTools : IToolSource
{
    public const string FindItems = "custos_find_items";
    public const string SetLabel = "custos_set_structure_label";

    public string Result { get; set; } =
        """{"items":[{"name":"Rifle","quantity":40,"structure_id":"s-1","label":"Seaport"}]}""";

    public string WriteResult { get; set; } = """{"new_label":"Main Depot"}""";

    public bool WriteFails { get; set; }

    public TaskCompletionSource? WriteGate { get; set; }

    public bool Available { get; set; } = true;

    public ConcurrentQueue<ToolCall> Calls { get; } = new();

    public ConcurrentQueue<(ToolUser User, WriteCall Call)> Writes { get; } = new();

    public Task<IToolSession> OpenAsync(ToolUser user, CancellationToken cancellationToken) =>
        Task.FromResult<IToolSession>(new Session(this));

    public async Task<ToolResult> CallWriteAsync(
        ToolUser user,
        WriteCall call,
        CancellationToken cancellationToken
    )
    {
        if (WriteGate is { } gate)
            await gate.Task.WaitAsync(cancellationToken);
        if (WriteFails)
            throw new HttpRequestException("The service did not answer.");
        Writes.Enqueue((user, call));
        return new ToolResult(call.Call.Id, WriteResult, false);
    }

    private sealed class Session(FakeTools tools) : IToolSession
    {
        public IReadOnlyList<ServiceCard> Services =>
            [new ServiceCard("custos", "Custos", "Custos holds the stockpiles.", tools.Available)];

        public IReadOnlyList<ModelTool> Tools =>
            tools.Available
                ?
                [
                    new ModelTool(
                        FindItems,
                        "Custos: Find items",
                        "Finds items.",
                        JsonDocument.Parse("""{"type":"object"}""").RootElement
                    ),
                    new ModelTool(
                        SetLabel,
                        "Custos: Set the label of a structure",
                        "Sets a label.",
                        JsonDocument.Parse("""{"type":"object"}""").RootElement,
                        Write: true
                    ),
                ]
                : [];

        public Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
        {
            tools.Calls.Enqueue(call);
            return Task.FromResult(new ToolResult(call.Id, tools.Result, false));
        }

        public WriteCall? AsWrite(ToolCall call)
        {
            if (call.Name != SetLabel)
                return null;
            var arguments = call.Arguments.ToDictionary();
            arguments.Remove(Questions.TextArgument);
            return new WriteCall(
                "custos",
                "Set the label of a structure",
                "Custos: Set the label of a structure",
                call with
                {
                    Name = "set_structure_label",
                    Arguments = arguments,
                },
                [arguments.TryGetValue("label", out var label) ? label.GetString() : null],
                [
                    .. arguments.TryGetValue("structure_id", out var id)
                        ? [id.GetString()!]
                        : Array.Empty<string>(),
                ]
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
