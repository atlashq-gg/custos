using System.Diagnostics.Metrics;

namespace Custos.Server.Telemetry;

/// <summary>The instruments of the server, on the meter of <see cref="CustosTelemetry"/>.</summary>
internal sealed class ServerMetrics
{
    public const string Uploads = "custos.uploads";
    public const string UploadParseDuration = "custos.upload.parse.duration";
    public const string WarApiAnswers = "custos.war_api.answers";
    public const string OutcomeTag = "outcome";

    private readonly Counter<long> _uploads;
    private readonly Histogram<double> _uploadParseDuration;
    private readonly Counter<long> _warApiAnswers;

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
    }

    /// <summary>
    /// Counts an upload that reached the handler. The outcome comes from the HTTP status of the
    /// answer. A refusal before the handler (401, 415, 429) is only in the metrics of ASP.NET Core.
    /// </summary>
    public void Upload(int status) => _uploads.Add(1, Outcome(UploadOutcome(status)));

    public void UploadParsed(TimeSpan duration) =>
        _uploadParseDuration.Record(duration.TotalSeconds);

    public void WarApiAnswer(WarApiOutcome outcome) =>
        _warApiAnswers.Add(1, Outcome(outcome.ToString().ToLowerInvariant()));

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
