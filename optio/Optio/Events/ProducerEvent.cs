using System.Globalization;
using System.Text.Json;
using Optio.Streams;

namespace Optio.Events;

/// <summary>
/// An event of a producer, as optio reads it from the stream of the source
/// (optio/docs/producers.md). <paramref name="Offset"/> is the offset in that stream.
/// </summary>
internal sealed record ProducerEvent(
    string Source,
    long Offset,
    DateTimeOffset Time,
    EventPolicy Policy,
    string Text,
    string? Reference
)
{
    /// <summary>The policy version that optio knows.</summary>
    public const int PolicyVersion = 1;

    /// <summary>
    /// Reads the CloudEvents document of a message. Null for a document that is not valid, or for
    /// a policy version that optio does not know. optio sends no such event.
    /// </summary>
    public static ProducerEvent? Parse(string source, long offset, ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var data = root.GetProperty("data");
            var policy = data.GetProperty("policy");
            var message = data.GetProperty("message");
            if (
                policy.GetProperty("version").GetInt32() != PolicyVersion
                || message.GetProperty("text").GetString() is not { Length: > 0 } text
            )
            {
                return null;
            }

            return new ProducerEvent(
                source,
                offset,
                root.GetProperty("time").GetDateTimeOffset(),
                new EventPolicy(
                    Id(policy.GetProperty("guild")),
                    [.. policy.GetProperty("roles").EnumerateArray().Select(Id)],
                    policy.TryGetProperty("users", out var users)
                        ? [.. users.EnumerateArray().Select(Id)]
                        : null
                ),
                text,
                message.TryGetProperty("reference", out var reference)
                    ? reference.GetString()
                    : null
            );
        }
        catch (Exception e)
            when (e
                    is JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
                        or OverflowException
                        or ArgumentException
            )
        {
            return null;
        }
    }

    private static ulong Id(JsonElement element) =>
        ulong.Parse(element.GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);
}
