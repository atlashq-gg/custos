using System.Diagnostics.Metrics;

namespace Optio.Telemetry;

/// <summary>The metrics of Optio, on the meter <see cref="TelemetrySetup.Name"/>.</summary>
internal sealed class OptioMetrics
{
    public const string Events = "optio.events";
    public const string EventAge = "optio.events.age";
    public const string Streams = "optio.streams";
    public const string OutcomeTag = "outcome";
    public const string SourceTag = "source";

    private readonly Counter<long> _events;
    private readonly Histogram<double> _eventAge;
    private readonly UpDownCounter<long> _streams;

    public OptioMetrics(IMeterFactory meterFactory)
    {
        Meter = meterFactory.Create(TelemetrySetup.Name, OptioVersion.Value);
        _events = Meter.CreateCounter<long>(
            Events,
            "{event}",
            "The events that the reader of each open stream got from the broker, by outcome: sent "
                + "(to the client), denied (the policy does not permit it), no_bot (the bot is not "
                + "in the guild), or invalid (not valid, or an unknown policy version)."
        );
        _eventAge = Meter.CreateHistogram<double>(
            EventAge,
            "s",
            "The age of each event when Optio sends it to a client. A high age in normal "
                + "operation shows that a client or Optio is slow."
        );
        _streams = Meter.CreateUpDownCounter<long>(Streams, "{stream}", "The open streams.");
    }

    public Meter Meter { get; }

    public void Event(string outcome) =>
        _events.Add(1, new KeyValuePair<string, object?>(OutcomeTag, outcome));

    public void EventAgeOf(string source, TimeSpan age) =>
        _eventAge.Record(age.TotalSeconds, new KeyValuePair<string, object?>(SourceTag, source));

    public void StreamOpened() => _streams.Add(1);

    public void StreamClosed() => _streams.Add(-1);
}
