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
/// One service <c>custos</c> with the read tool <c>custos_find_items</c>. A call gives
/// <see cref="Result"/>. With <see cref="Available"/> false, the service has no tools.
/// </summary>
internal sealed class FakeTools : IToolSource
{
    public const string FindItems = "custos_find_items";

    public string Result { get; set; } = """{"items":[{"name":"Rifle","quantity":40}]}""";

    public bool Available { get; set; } = true;

    public ConcurrentQueue<ToolCall> Calls { get; } = new();

    public Task<IToolSession> OpenAsync(ToolUser user, CancellationToken cancellationToken) =>
        Task.FromResult<IToolSession>(new Session(this));

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
                ]
                : [];

        public Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
        {
            tools.Calls.Enqueue(call);
            return Task.FromResult(new ToolResult(call.Id, tools.Result, false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
