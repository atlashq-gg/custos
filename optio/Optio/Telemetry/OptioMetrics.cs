using System.Diagnostics.Metrics;

namespace Optio.Telemetry;

/// <summary>The metrics of optio, on the meter <see cref="TelemetrySetup.Name"/>.</summary>
internal sealed class OptioMetrics
{
    public const string Events = "optio.events";
    public const string EventAge = "optio.events.age";
    public const string EventOffset = "optio.events.offset";
    public const string Streams = "optio.streams";
    public const string StreamDropped = "optio.stream.dropped";
    public const string OutcomeTag = "outcome";
    public const string SourceTag = "source";

    private readonly Counter<long> _events;
    private readonly Histogram<double> _eventAge;
    private readonly UpDownCounter<long> _streams;
    private readonly Counter<long> _streamDropped;

    public OptioMetrics(IMeterFactory meterFactory)
    {
        Meter = meterFactory.Create(TelemetrySetup.Name, OptioVersion.Value);
        _events = Meter.CreateCounter<long>(
            Events,
            "{event}",
            "The events from the broker, by outcome: delivered (to one stream or more), no_stream "
                + "(no open stream can read it), no_bot (the bot is not in the guild), or ignored "
                + "(not valid, or an unknown policy version)."
        );
        _eventAge = Meter.CreateHistogram<double>(
            EventAge,
            "s",
            "The age of each event when optio reads it from the broker. A high age shows that "
                + "optio is behind the end of the stream."
        );
        _streams = Meter.CreateUpDownCounter<long>(Streams, "{stream}", "The open streams.");
        _streamDropped = Meter.CreateCounter<long>(
            StreamDropped,
            "{event}",
            "The events that a stream dropped, because its client did not read them in time."
        );
    }

    public Meter Meter { get; }

    public void Event(string outcome) =>
        _events.Add(1, new KeyValuePair<string, object?>(OutcomeTag, outcome));

    public void EventAgeOf(string source, TimeSpan age) =>
        _eventAge.Record(age.TotalSeconds, new KeyValuePair<string, object?>(SourceTag, source));

    /// <summary>The last offset that optio read from the stream of each source.</summary>
    public void ObserveOffsets(Func<IEnumerable<Measurement<long>>> observe) =>
        Meter.CreateObservableGauge(
            EventOffset,
            observe,
            "{offset}",
            "The last offset that optio read from the stream of each source."
        );

    public void StreamOpened() => _streams.Add(1);

    public void StreamClosed() => _streams.Add(-1);

    public void StreamDroppedEvent() => _streamDropped.Add(1);
}
