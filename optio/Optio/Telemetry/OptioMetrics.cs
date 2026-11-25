using System.Diagnostics.Metrics;

namespace Optio.Telemetry;

/// <summary>The metrics of Optio, on the meter <see cref="TelemetrySetup.Name"/>.</summary>
internal sealed class OptioMetrics
{
    public const string Events = "optio.events";
    public const string EventAge = "optio.events.age";
    public const string Streams = "optio.streams";
    public const string Queries = "optio.queries";
    public const string ToolCalls = "optio.tool.calls";
    public const string CauseTag = "cause";
    public const string ServiceTag = "service";
    public const string ToolTag = "tool";
    public const string OutcomeTag = "outcome";
    public const string SourceTag = "source";

    private readonly Counter<long> _events;
    private readonly Histogram<double> _eventAge;
    private readonly UpDownCounter<long> _streams;
    private readonly Counter<long> _queries;
    private readonly Counter<long> _toolCalls;

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
        _queries = Meter.CreateCounter<long>(
            Queries,
            "{query}",
            "The queries that closed, by the cause of spec/06-query.md, for example answer, "
                + "failed, or interrupted."
        );
        _toolCalls = Meter.CreateCounter<long>(
            ToolCalls,
            "{call}",
            "The tool calls of the queries, by service, tool, and outcome: ok, error (the tool "
                + "gave an error), or failed (no result from the service)."
        );
    }

    public Meter Meter { get; }

    public void Event(string outcome) =>
        _events.Add(1, new KeyValuePair<string, object?>(OutcomeTag, outcome));

    public void EventAgeOf(string source, TimeSpan age) =>
        _eventAge.Record(age.TotalSeconds, new KeyValuePair<string, object?>(SourceTag, source));

    public void StreamOpened() => _streams.Add(1);

    public void StreamClosed() => _streams.Add(-1);

    public void QueryClosed(string cause) =>
        _queries.Add(1, new KeyValuePair<string, object?>(CauseTag, cause));

    public void ToolCall(string service, string tool, string outcome) =>
        _toolCalls.Add(
            1,
            new KeyValuePair<string, object?>(ServiceTag, service),
            new KeyValuePair<string, object?>(ToolTag, tool),
            new KeyValuePair<string, object?>(OutcomeTag, outcome)
        );
}
