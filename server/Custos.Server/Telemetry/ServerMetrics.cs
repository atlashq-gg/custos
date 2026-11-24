using System.Diagnostics;
using System.Diagnostics.Metrics;
using Custos.Server.Events;

namespace Custos.Server.Telemetry;

/// <summary>The instruments of the server, on the meter of <see cref="CustosTelemetry"/>.</summary>
internal sealed class ServerMetrics
{
    public const string Uploads = "custos.uploads";
    public const string UploadParseDuration = "custos.upload.parse.duration";
    public const string WarApiAnswers = "custos.war_api.answers";
    public const string DiscordRequestDuration = "custos.discord.request.duration";
    public const string McpToolCalls = "custos.mcp.tool.calls";
    public const string McpToolDuration = "custos.mcp.tool.duration";
    public const string Events = "custos.events";
    public const string OutcomeTag = "outcome";

    private readonly Counter<long> _uploads;
    private readonly Histogram<double> _uploadParseDuration;
    private readonly Counter<long> _warApiAnswers;
    private readonly Histogram<double> _discordRequestDuration;
    private readonly Counter<long> _mcpToolCalls;
    private readonly Histogram<double> _mcpToolDuration;
    private readonly Counter<long> _events;

    public ServerMetrics(Meter meter)
    {
        _uploads = meter.CreateCounter<long>(
            Uploads,
            "{upload}",
            "The uploads that reached the handler, by outcome."
        );
        _uploadParseDuration = meter.CreateHistogram<double>(
            UploadParseDuration,
            "s",
            "The time to parse the map data file of an upload."
        );
        _warApiAnswers = meter.CreateCounter<long>(
            WarApiAnswers,
            "{answer}",
            "The requests for the current war to the war API, by outcome: fresh, stale (the "
                + "server used the last answer), or none."
        );
        _discordRequestDuration = meter.CreateHistogram<double>(
            DiscordRequestDuration,
            "s",
            "The requests of the bot to the REST API of Discord, by outcome: ok, client_error, "
                + "rate_limited (429), server_error (5xx), or error (no answer)."
        );
        _mcpToolCalls = meter.CreateCounter<long>(
            McpToolCalls,
            "{call}",
            "The calls of MCP tools, by tool, outcome, and access of the caller."
        );
        _mcpToolDuration = meter.CreateHistogram<double>(
            McpToolDuration,
            "s",
            "The duration of the calls of MCP tools, by tool, outcome, and access of the caller."
        );
        _events = meter.CreateCounter<long>(
            Events,
            "{event}",
            "The events for the broker, by outcome: published, returned (no stream got it), "
                + "dropped (the queue was full), or failed (the broker did not accept it)."
        );
    }

    public void Event(EventOutcome outcome) =>
        _events.Add(1, Outcome(outcome.ToString().ToLowerInvariant()));

    /// <summary>
    /// Counts an upload that reached the handler. The outcome comes from the HTTP status of the
    /// answer. A refusal before the handler (401, 415, 429) is only in the metrics of ASP.NET Core.
    /// </summary>
    public void Upload(int status) => _uploads.Add(1, Outcome(UploadOutcome(status)));

    public void UploadParsed(TimeSpan duration) =>
        _uploadParseDuration.Record(duration.TotalSeconds);

    public void WarApiAnswer(WarApiOutcome outcome) =>
        _warApiAnswers.Add(1, Outcome(outcome.ToString().ToLowerInvariant()));

    /// <summary>Records a request to Discord. <paramref name="status"/> is <c>null</c> without an
    /// answer.</summary>
    public void DiscordRequest(TimeSpan duration, int? status) =>
        _discordRequestDuration.Record(duration.TotalSeconds, Outcome(DiscordOutcome(status)));

    public void McpToolCall(string tool, string outcome, string access, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "tool", tool },
            { OutcomeTag, outcome },
            { "access", access },
        };
        _mcpToolCalls.Add(1, tags);
        _mcpToolDuration.Record(duration.TotalSeconds, tags);
    }

    internal static string DiscordOutcome(int? status) =>
        status switch
        {
            null => "error",
            429 => "rate_limited",
            >= 500 => "server_error",
            >= 400 => "client_error",
            _ => "ok",
        };

    internal static string UploadOutcome(int status) =>
        status switch
        {
            StatusCodes.Status200OK => "accepted",
            StatusCodes.Status403Forbidden => "forbidden",
            StatusCodes.Status413PayloadTooLarge => "too_large",
            StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
            StatusCodes.Status422UnprocessableEntity => "not_map_data",
            StatusCodes.Status503ServiceUnavailable => "unavailable",
            _ => "error",
        };

    private static KeyValuePair<string, object?> Outcome(string value) => new(OutcomeTag, value);
}

internal enum WarApiOutcome
{
    Fresh,
    Stale,
    None,
}
