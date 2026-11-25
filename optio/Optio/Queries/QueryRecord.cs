using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optio.Queries;

/// <summary>The states and the closing causes of spec/06-query.md.</summary>
internal static class QueryStates
{
    public const string Working = "working";
    public const string Waiting = "waiting";
    public const string Applying = "applying";
    public const string Closed = "closed";

    public const string Answer = "answer";
    public const string Refused = "refused";
    public const string Expired = "expired";
    public const string Replaced = "replaced";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";
    public const string ResultUnknown = "result_unknown";
}

/// <summary>
/// The client of a query: one login of Custos in one guild. <paramref name="Key"/> is a hash of the
/// claim <c>sid</c>. It names the record of the client and is the last token of the subject of
/// its query messages.
/// </summary>
internal sealed record QueryClient(string Key, ulong Guild, ulong User)
{
    /// <summary>The subject of the query messages of the client.</summary>
    public string Subject =>
        string.Create(CultureInfo.InvariantCulture, $"queries.{Guild}.{User}.{Key}");

    /// <summary>The client of a token with the claim <c>sid</c>, else null.</summary>
    public static QueryClient? Of(ClaimsPrincipal user, ulong guild, ulong userId) =>
        user.FindFirst("sid")?.Value is { Length: > 0 } sid
            ? new QueryClient(KeyOf(sid), guild, userId)
            : null;

    /// <summary>The first 128 bits of the SHA-256 of the session, as hexadecimal.</summary>
    public static string KeyOf(string sid) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..32];
}

/// <summary>
/// The record of the last query of a client in the bucket. It is the truth about the query. Each
/// change of state is a compare-and-set, and only the winner publishes the message of the change.
/// <paramref name="Owner"/> is the instance of Optio that runs the query.
/// </summary>
internal sealed record QueryRecord(
    string Reference,
    string Client,
    ulong Guild,
    ulong User,
    string Question,
    bool NewConversation,
    string State,
    string? Text,
    DateTimeOffset? Deadline,
    string? Cause,
    string Owner,
    DateTimeOffset Created
)
{
    public bool IsOpen => State != QueryStates.Closed;

    /// <summary>The record in the state <c>closed</c>.</summary>
    public QueryRecord Close(string cause, string text) =>
        this with
        {
            State = QueryStates.Closed,
            Cause = cause,
            Text = text,
            Deadline = null,
        };

    /// <summary>The stream message of the state (spec/06-query.md).</summary>
    public QueryMessage Message() => new(Reference, State, Text, Deadline, Cause);
}

/// <summary>A stream message of the kind <c>query</c>, and the state document of a response with
/// no <c>kind</c> and <c>source</c>.</summary>
internal sealed record QueryMessage(
    string Reference,
    string State,
    string? Text,
    DateTimeOffset? Deadline,
    string? Cause
)
{
    public const string Source = "optio";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The message as one line of JSON for the stream.</summary>
    public byte[] ToStream() =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = "query",
                reference = Reference,
                state = State,
                text = Text,
                deadline = Deadline,
                cause = Cause,
                source = Source,
            },
            Json
        );

    /// <summary>The state document of a response: no text.</summary>
    public object ToState() =>
        new
        {
            reference = Reference,
            state = State,
            deadline = Deadline,
            cause = Cause,
        };
}
