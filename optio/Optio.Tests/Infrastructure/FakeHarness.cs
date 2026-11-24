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
        Call([.. tools.Select(t => (t, new Dictionary<string, object?>()))]);

    /// <summary>A turn with a call of each tool, with its arguments.</summary>
    public static ModelTurn Call(
        params (string Tool, Dictionary<string, object?> Arguments)[] calls
    ) =>
        new(
            "",
            [
                .. calls.Select(
                    (c, i) =>
                        new ToolCall(
                            $"call-{i}",
                            c.Tool,
                            c.Arguments.ToDictionary(
                                a => a.Key,
                                a => JsonSerializer.SerializeToElement(a.Value)
                            )
                        )
                ),
            ],
            ModelStop.ToolCalls,
            new object()
        );
}

/// <summary>The read tool <c>find_items</c>, which answers with its name and the guild, and the
/// write tool <c>set_structure_label</c>. A write call answers with an error when
/// <see cref="WriteError"/> has a value.</summary>
internal sealed class FakeTools : IToolSource
{
    public const string Tool = "find_items";
    public const string WriteTool = "set_structure_label";
    public const string WriteTitle = "Set the label of a structure";

    public Channel<(ulong Guild, string Tool)> Calls { get; } =
        Channel.CreateUnbounded<(ulong, string)>();

    /// <summary>The access token and the write call of each call of a write tool.</summary>
    public Channel<(string AccessToken, WriteCall Call)> Writes { get; } =
        Channel.CreateUnbounded<(string, WriteCall)>();

    public string? WriteError { get; set; }

    public Task<IToolSession> OpenAsync(ToolUser user, CancellationToken cancellationToken) =>
        Task.FromResult<IToolSession>(new Session(this, user.Guild));

    public Task<ToolResult> CallAsync(
        ToolUser user,
        WriteCall call,
        CancellationToken cancellationToken
    )
    {
        Writes.Writer.TryWrite((user.AccessToken, call));
        return Task.FromResult(
            new ToolResult(call.Call.Id, WriteError ?? "{}", WriteError is not null)
        );
    }

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
            new(
                WriteTool,
                WriteTitle,
                "Change the label of a structure.",
                JsonSerializer.SerializeToElement(new { type = "object" }),
                Write: true
            ),
        ];

        public Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
        {
            tools.Calls.Writer.TryWrite((guild, call.Name));
            return Task.FromResult(new ToolResult(call.Id, $"{call.Name} in {guild}", false));
        }

        public WriteCall? AsWrite(ToolCall call) =>
            call.Name == WriteTool ? new WriteCall("server", WriteTitle, call, ["label"]) : null;

        public ValueTask DisposeAsync() => default;
    }
}
