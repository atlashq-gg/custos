using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace Optio.Queries;

/// <summary>
/// The state of the queries on the broker (optio/docs/producers.md). The bucket
/// <see cref="QueriesBucket"/> holds the record of the last query of each client, and the client
/// of the open query of each user. The bucket <see cref="ConversationsBucket"/> holds the
/// conversation of each client for the follow-up window. Optio makes the buckets at the first use.
/// The messages of a query go to the stream of the events, below the subject of the client.
/// </summary>
internal sealed partial class QueryStore(
    NatsConnection connection,
    IOptions<OptioOptions> options,
    ILogger<QueryStore> logger
)
{
    public const string QueriesBucket = "OPTIO_QUERIES";
    public const string ConversationsBucket = "OPTIO_CONVERSATIONS";

    /// <summary>The broker removes a record with no change in this time. A query is open for a
    /// much shorter time.</summary>
    public static readonly TimeSpan RecordAge = TimeSpan.FromHours(1);

    private const int Attempts = 5;

    private readonly NatsJSContext _js = new(connection);
    private readonly SemaphoreSlim _made = new(1, 1);
    private INatsKVStore? _queries;
    private INatsKVStore? _conversations;

    public static NatsKVConfig QueriesConfig() =>
        new(QueriesBucket)
        {
            History = 1,
            MaxAge = RecordAge,
            Storage = NatsKVStorageType.File,
        };

    public static NatsKVConfig ConversationsConfig(OptioOptions options) =>
        new(ConversationsBucket)
        {
            History = 1,
            MaxAge = options.FollowUpWindow,
            MaxValueSize = options.MaxConversationBytes,
            Storage = NatsKVStorageType.File,
        };

    /// <summary>The record of the last query of a client, with its revision. Revision 0: no
    /// record.</summary>
    public async Task<(QueryRecord? Record, ulong Revision)> GetAsync(
        string client,
        CancellationToken cancellationToken
    )
    {
        var (queries, _) = await StoresAsync(cancellationToken);
        var entry = await queries.TryGetEntryAsync<byte[]>(
            ClientKey(client),
            cancellationToken: cancellationToken
        );
        return entry.Success && entry.Value.Value is { Length: > 0 } value
            ? (
                JsonSerializer.Deserialize<QueryRecord>(value, QueryMessage.Json),
                entry.Value.Revision
            )
            : (null, entry.Success ? entry.Value.Revision : 0);
    }

    /// <summary>Writes the first record of a client, or the next one after
    /// <paramref name="revision"/>. Null when another writer was first.</summary>
    public async Task<ulong?> TryPutAsync(
        QueryRecord record,
        ulong revision,
        CancellationToken cancellationToken
    )
    {
        var (queries, _) = await StoresAsync(cancellationToken);
        var value = JsonSerializer.SerializeToUtf8Bytes(record, QueryMessage.Json);
        var key = ClientKey(record.Client);
        var result =
            revision == 0
                ? await queries.TryCreateAsync(key, value, cancellationToken: cancellationToken)
                : await queries.TryUpdateAsync(
                    key,
                    value,
                    revision,
                    cancellationToken: cancellationToken
                );
        return result.Success ? result.Value : null;
    }

    /// <summary>
    /// Changes the record of <paramref name="reference"/> with <paramref name="change"/>, with a
    /// new try after a conflict. The change gives null when it does not apply. On success, the
    /// store publishes the message of the new record. Null when the record is of another query, or
    /// when the change does not apply.
    /// </summary>
    public async Task<QueryRecord?> ChangeAsync(
        string client,
        string reference,
        Func<QueryRecord, QueryRecord?> change,
        CancellationToken cancellationToken
    )
    {
        for (var i = 0; i < Attempts; i++)
        {
            var (record, revision) = await GetAsync(client, cancellationToken);
            if (record?.Reference != reference || change(record) is not { } next)
                return null;
            if (await TryPutAsync(next, revision, cancellationToken) is not null)
            {
                await PublishAsync(next, cancellationToken);
                return next;
            }
        }
        throw new InvalidOperationException("The record of the query changes too often.");
    }

    /// <summary>
    /// Makes <paramref name="client"/> the client of the open query of its user. False when
    /// another client of the user has an open query.
    /// </summary>
    public async Task<bool> ClaimUserAsync(QueryClient client, CancellationToken cancellationToken)
    {
        var (queries, _) = await StoresAsync(cancellationToken);
        var key = UserKey(client.User);
        var value = System.Text.Encoding.UTF8.GetBytes(client.Key);
        for (var i = 0; i < Attempts; i++)
        {
            var entry = await queries.TryGetEntryAsync<byte[]>(
                key,
                cancellationToken: cancellationToken
            );
            if (!entry.Success || entry.Value.Value is not { Length: > 0 } holder)
            {
                if (
                    (
                        await queries.TryCreateAsync(
                            key,
                            value,
                            cancellationToken: cancellationToken
                        )
                    ).Success
                )
                    return true;
                continue;
            }

            var other = System.Text.Encoding.UTF8.GetString(holder);
            if (other == client.Key)
                return true;
            // A record that is closed, or that the broker removed, holds no claim.
            if ((await GetAsync(other, cancellationToken)).Record is { IsOpen: true })
                return false;
            var update = await queries.TryUpdateAsync(
                key,
                value,
                entry.Value.Revision,
                cancellationToken: cancellationToken
            );
            if (update.Success)
                return true;
        }
        return false;
    }

    /// <summary>Removes the claim of the client on its user.</summary>
    public async Task ReleaseUserAsync(QueryClient client, CancellationToken cancellationToken)
    {
        var (queries, _) = await StoresAsync(cancellationToken);
        var key = UserKey(client.User);
        var entry = await queries.TryGetEntryAsync<byte[]>(
            key,
            cancellationToken: cancellationToken
        );
        if (
            entry.Success
            && entry.Value.Value is { Length: > 0 } holder
            && System.Text.Encoding.UTF8.GetString(holder) == client.Key
        )
        {
            await queries.TryDeleteAsync(
                key,
                new NatsKVDeleteOpts { Revision = entry.Value.Revision },
                cancellationToken
            );
        }
    }

    /// <summary>The records of all clients, for the recovery at the start.</summary>
    public async IAsyncEnumerable<QueryRecord> RecordsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var (queries, _) = await StoresAsync(cancellationToken);
        List<string> keys = [];
        try
        {
            await foreach (
                var key in queries.GetKeysAsync(["client.>"], cancellationToken: cancellationToken)
            )
                keys.Add(key);
        }
        catch (NatsKVException)
        {
            // An empty bucket.
        }
        foreach (var key in keys)
        {
            if ((await GetAsync(key["client.".Length..], cancellationToken)).Record is { } record)
                yield return record;
        }
    }

    /// <summary>Publishes the message of a record to the stream. A failure only goes to the log:
    /// the record holds the state, and a stream that opens sends it.</summary>
    public async Task PublishAsync(QueryRecord record, CancellationToken cancellationToken)
    {
        var subject = new QueryClient(record.Client, record.Guild, record.User).Subject;
        try
        {
            var ack = await _js.PublishAsync(
                subject,
                record.Message().ToStream(),
                cancellationToken: cancellationToken
            );
            ack.EnsureSuccess();
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            LogPublishFailed(logger, e, record.Reference);
        }
    }

    public async Task<Conversation?> GetConversationAsync(
        string client,
        CancellationToken cancellationToken
    )
    {
        var (_, conversations) = await StoresAsync(cancellationToken);
        var entry = await conversations.TryGetEntryAsync<byte[]>(
            client,
            cancellationToken: cancellationToken
        );
        return entry.Success && entry.Value.Value is { Length: > 0 } value
            ? JsonSerializer.Deserialize<Conversation>(value, QueryMessage.Json)
            : null;
    }

    /// <summary>Adds the exchange of a query to the conversation of its client. A new
    /// conversation starts empty.</summary>
    public async Task AddExchangeAsync(
        string client,
        bool newConversation,
        Exchange exchange,
        CancellationToken cancellationToken
    )
    {
        var (_, conversations) = await StoresAsync(cancellationToken);
        var old = newConversation ? null : await GetConversationAsync(client, cancellationToken);
        if (Conversation.Append(old, exchange, options.Value.MaxConversationBytes) is { } value)
            await conversations.PutAsync(client, value, cancellationToken: cancellationToken);
        else
            await conversations.TryDeleteAsync(client, cancellationToken: cancellationToken);
    }

    private static string ClientKey(string client) => $"client.{client}";

    private static string UserKey(ulong user) =>
        string.Create(CultureInfo.InvariantCulture, $"user.{user}");

    /// <summary>The buckets. The first use makes them, or sets their settings again.</summary>
    private async ValueTask<(INatsKVStore Queries, INatsKVStore Conversations)> StoresAsync(
        CancellationToken cancellationToken
    )
    {
        if (_queries is not null && _conversations is not null)
            return (_queries, _conversations);
        await _made.WaitAsync(cancellationToken);
        try
        {
            var kv = new NatsKVContext(_js);
            _queries ??= await kv.CreateOrUpdateStoreAsync(QueriesConfig(), cancellationToken);
            _conversations ??= await kv.CreateOrUpdateStoreAsync(
                ConversationsConfig(options.Value),
                cancellationToken
            );
            return (_queries, _conversations);
        }
        finally
        {
            _made.Release();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The message of the query {Reference} did not go to the stream"
    )]
    private static partial void LogPublishFailed(
        ILogger logger,
        Exception exception,
        string reference
    );
}
