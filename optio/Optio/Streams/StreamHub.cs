using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Optio.Discord;
using Optio.Events;
using Optio.Telemetry;

namespace Optio.Streams;

/// <summary>
/// The events of the replay window and the open streams. The consumers give each event to
/// <see cref="Publish"/>, and the hub sends it to each open stream whose user can read it. One
/// lock keeps the log, the offsets, and the streams together, so that a new stream gets each
/// event one time: from the replay, or live.
/// </summary>
internal sealed class StreamHub
{
    /// <summary>A stream keeps at most this number of events that its client did not read. When
    /// the client is slower, the oldest event goes.</summary>
    public const int StreamCapacity = 1000;

    private readonly object _lock = new();
    private readonly LinkedList<StreamEvent> _log = [];
    private readonly Dictionary<string, long> _offsets = [];
    private readonly HashSet<Subscriber> _subscribers = [];
    private readonly IMemberDirectory _members;
    private readonly TimeProvider _time;
    private readonly OptioMetrics _metrics;
    private readonly TimeSpan _window;
    private readonly int _maxPerUser;

    public StreamHub(
        IMemberDirectory members,
        IOptions<OptioOptions> options,
        TimeProvider time,
        OptioMetrics metrics
    )
    {
        _members = members;
        _time = time;
        _metrics = metrics;
        _window = options.Value.ReplayWindow;
        _maxPerUser = options.Value.MaxStreamsPerUser;
        _members.GuildReady += Release;
        _metrics.ObserveOffsets(() =>
        {
            lock (_lock)
            {
                return
                [
                    .. _offsets.Select(o => new Measurement<long>(
                        o.Value,
                        new KeyValuePair<string, object?>(OptioMetrics.SourceTag, o.Key)
                    )),
                ];
            }
        });
    }

    /// <summary>The SSE <c>id</c> of now: the last offset of each source.</summary>
    public string CurrentId
    {
        get
        {
            lock (_lock)
                return Cursor.Encode(_offsets);
        }
    }

    public void Publish(ProducerEvent producerEvent)
    {
        _metrics.EventAgeOf(producerEvent.Source, _time.GetUtcNow() - producerEvent.Time);
        lock (_lock)
        {
            _offsets[producerEvent.Source] = producerEvent.Offset;
            var streamEvent = new StreamEvent(producerEvent, Cursor.Encode(_offsets));
            _log.AddLast(streamEvent);
            Prune();

            if (!_members.HasGuild(producerEvent.Policy.Guild))
            {
                _metrics.Event("no_bot");
                return;
            }
            var delivered = false;
            foreach (var subscriber in _subscribers)
                delivered |= subscriber.Offer(streamEvent, _members);
            _metrics.Event(delivered ? "delivered" : "no_stream");
        }
    }

    /// <summary>
    /// Opens a stream for a member of a guild. The stream first gets the events after
    /// <paramref name="lastEventId"/> that the member can read. Null when the user has too many
    /// open streams.
    /// </summary>
    public Subscriber? Subscribe(ulong guild, ulong user, string? lastEventId)
    {
        lock (_lock)
        {
            if (_subscribers.Count(s => s.UserId == user) >= _maxPerUser)
                return null;

            var subscriber = new Subscriber(guild, user, _metrics);
            Prune();
            if (Start(lastEventId) is { } start)
            {
                foreach (var streamEvent in _log)
                {
                    // A source that the id does not name starts from now.
                    if (
                        start.TryGetValue(streamEvent.Event.Source, out var offset)
                        && streamEvent.Event.Offset > offset
                    )
                    {
                        subscriber.Offer(streamEvent, _members);
                    }
                }
            }
            _subscribers.Add(subscriber);
            _metrics.StreamOpened();
            return subscriber;
        }
    }

    public void Unsubscribe(Subscriber subscriber)
    {
        lock (_lock)
        {
            if (_subscribers.Remove(subscriber))
                _metrics.StreamClosed();
        }
    }

    /// <summary>The offsets of a valid id. An id that is not valid, or that names an offset after
    /// the last offset of its source, starts from now: for example after the broker made the
    /// stream again.</summary>
    private Dictionary<string, long>? Start(string? lastEventId)
    {
        if (Cursor.Decode(lastEventId) is not { } start)
            return null;
        return start.Any(o => o.Value > _offsets.GetValueOrDefault(o.Key, long.MaxValue))
            ? null
            : start;
    }

    /// <summary>Removes the events that are older than the window.</summary>
    private void Prune()
    {
        var oldest = _time.GetUtcNow() - _window;
        while (_log.First is { } first && first.Value.Event.Time < oldest)
            _log.RemoveFirst();
    }

    /// <summary>Gives the held events of a guild that became ready to its streams.</summary>
    private void Release(ulong guild)
    {
        lock (_lock)
        {
            foreach (var subscriber in _subscribers.Where(s => s.Guild == guild))
                subscriber.Release(_members);
        }
    }
}

/// <summary>An event with its SSE <c>id</c>: the offset of each source after it.</summary>
internal sealed record StreamEvent(ProducerEvent Event, string Id);

/// <summary>One open stream: the events for its client, in order.</summary>
internal sealed class Subscriber(ulong guild, ulong userId, OptioMetrics metrics)
{
    private readonly Channel<StreamEvent> _events = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(StreamHub.StreamCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        },
        _ => metrics.StreamDroppedEvent()
    );

    // The events that wait until the guild is ready. Only the hub changes it, under its lock.
    private readonly List<StreamEvent> _held = [];

    public ulong Guild { get; } = guild;

    public ulong UserId { get; } = userId;

    public ChannelReader<StreamEvent> Events => _events.Reader;

    /// <summary>Gives the event to the client when the member can read it. Holds it when the
    /// guild is not ready.</summary>
    public bool Offer(StreamEvent streamEvent, IMemberDirectory members)
    {
        if (streamEvent.Event.Policy.Guild != Guild)
            return false;
        switch (members.CanRead(streamEvent.Event.Policy, Guild, UserId))
        {
            case true:
                _events.Writer.TryWrite(streamEvent);
                return true;
            case null:
                _held.Add(streamEvent);
                return false;
            default:
                return false;
        }
    }

    public void Release(IMemberDirectory members)
    {
        var held = _held.ToList();
        _held.Clear();
        foreach (var streamEvent in held)
            Offer(streamEvent, members);
    }
}

/// <summary>The SSE <c>id</c>: the offset of each source as base64url JSON, for example
/// <c>{"custos":142}</c>. Custos keeps it opaque.</summary>
internal static class Cursor
{
    private const int MaxLength = 4096;

    public static string Encode(IReadOnlyDictionary<string, long> offsets) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(offsets));

    public static Dictionary<string, long>? Decode(string? id)
    {
        if (id is null || id.Length > MaxLength)
            return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, long>>(
                Base64Url.DecodeFromChars(id)
            );
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }
}
