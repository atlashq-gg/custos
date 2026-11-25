using System.Globalization;

namespace Optio.Events;

/// <summary>
/// A position in the stream of the events: the identity of the stream and a sequence. It is the
/// SSE <c>id</c> of spec/05-stream.md, for example <c>1764090192512-142</c>. The identity is the
/// creation time of the stream in Unix milliseconds, so that a stream that an operator makes again
/// does not accept an old position.
/// </summary>
internal readonly record struct Position(long Stream, ulong Sequence)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Stream}-{Sequence}");

    /// <summary>
    /// The position after which a reader starts. A position of <paramref name="now"/>'s stream
    /// that is not after its end gives itself. Each other value starts from now: no position, a
    /// position that is not valid, of another stream, or after the end.
    /// </summary>
    public static Position Start(string? lastEventId, Position now) =>
        Parse(lastEventId) is { } last && last.Stream == now.Stream && last.Sequence <= now.Sequence
            ? last
            : now;

    public static Position? Parse(string? id)
    {
        var dash = id?.IndexOf('-') ?? -1;
        if (id is null || id.Length > 41 || dash < 1)
            return null;
        return
            long.TryParse(
                id.AsSpan(0, dash),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var stream
            )
            && ulong.TryParse(
                id.AsSpan(dash + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence
            )
            ? new Position(stream, sequence)
            : null;
    }
}
