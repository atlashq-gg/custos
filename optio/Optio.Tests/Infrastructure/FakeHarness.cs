using System.Text.Json;
using System.Threading.Channels;
using Optio.Harness;

namespace Optio.Tests.Infrastructure;

/// <summary>A model that gives the turns of <see cref="Turns"/> in order, and keeps each
/// request. With no turn left, it waits until the query ends.</summary>
internal sealed class FakeModel : IModelClient
{
    public Queue<ModelTurn> Turns { get; } = new();

    public List<ModelRequest> Requests { get; } = [];

    public async Task<ModelTurn> NextAsync(
        ModelRequest request,
        CancellationToken cancellationToken
    )
    {
        lock (Requests)
            Requests.Add(request with { Messages = [.. request.Messages] });
        lock (Turns)
        {
            if (Turns.TryDequeue(out var turn))
                return turn;
        }
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException();
    }

    public static ModelTurn Answer(string text) => new(text, [], ModelStop.End, new object());

    public static ModelTurn Call(params string[] tools) =>
        new(
            "",
            [
                .. tools.Select(
                    (t, i) => new ToolCall($"call-{i}", t, new Dictionary<string, JsonElement>())
                ),
            ],
            ModelStop.ToolCalls,
            new object()
        );
}

/// <summary>The tool <c>find_items</c>, which answers with its name and the guild.</summary>
internal sealed class FakeTools : IToolSource
{
    public const string Tool = "find_items";

    public Channel<(ulong Guild, string Tool)> Calls { get; } =
        Channel.CreateUnbounded<(ulong, string)>();

    public Task<IToolSession> OpenAsync(
        string accessToken,
        ulong guild,
        CancellationToken cancellationToken
    ) => Task.FromResult<IToolSession>(new Session(this, guild));

    private sealed class Session(FakeTools tools, ulong guild) : IToolSession
    {
        public IReadOnlyList<ModelTool> Tools { get; } =
        [
            new(
                Tool,
                "Find items",
                "The stockpiles that hold an item.",
                JsonSerializer.SerializeToElement(new { type = "object" })
            ),
        ];

        public Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
        {
            tools.Calls.Writer.TryWrite((guild, call.Name));
            return Task.FromResult(new ToolResult(call.Id, $"{call.Name} in {guild}", false));
        }

        public ValueTask DisposeAsync() => default;
    }
}
