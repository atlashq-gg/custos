using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Optio.Streams;

namespace Optio.Events;

/// <summary>
/// An event of a producer, as Optio reads it from the stream (optio/docs/producers.md).
/// <paramref name="Sequence"/> is its sequence in the stream.
/// </summary>
internal sealed partial record ProducerEvent(
    string Source,
    ulong Sequence,
    DateTimeOffset Time,
    EventPolicy Policy,
    string Text,
    string? Reference
)
{
    /// <summary>The policy version that Optio knows.</summary>
    public const int PolicyVersion = 1;

    /// <summary>The filter of a reader: the events of all sources for one guild.</summary>
    public static string Filter(ulong guild) =>
        string.Create(CultureInfo.InvariantCulture, $"events.*.{guild}.>");

    /// <summary>
    /// Reads the CloudEvents document of a message and its subject
    /// <c>events.&lt;source&gt;.&lt;guild&gt;.&lt;type&gt;</c>. The source comes from the subject,
    /// which only the producer of that source can publish below. Null for an event that is not
    /// valid, that does not agree with its subject, or that has a policy version that Optio does
    /// not know. Optio sends no such event.
    /// </summary>
    public static ProducerEvent? Parse(string subject, ulong sequence, ReadOnlyMemory<byte> body)
    {
        if (Subject().Match(subject) is not { Success: true } match)
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var data = root.GetProperty("data");
            var policy = data.GetProperty("policy");
            var message = data.GetProperty("message");
            var guild = Id(policy.GetProperty("guild"));
            if (
                root.GetProperty("source").GetString() != match.Groups["source"].Value
                || root.GetProperty("type").GetString() != match.Groups["type"].Value
                || guild.ToString(CultureInfo.InvariantCulture) != match.Groups["guild"].Value
                || policy.GetProperty("version").GetInt32() != PolicyVersion
                || message.GetProperty("text").GetString() is not { Length: > 0 } text
            )
            {
                return null;
            }

            return new ProducerEvent(
                match.Groups["source"].Value,
                sequence,
                root.GetProperty("time").GetDateTimeOffset(),
                new EventPolicy(
                    guild,
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

    [GeneratedRegex(
        @"^events\.(?<source>[a-z0-9][a-z0-9-]*)\.(?<guild>[0-9]{1,20})\."
            + @"(?<type>[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*)$"
    )]
    private static partial Regex Subject();
}
