using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Optio.Discord;
using Optio.Events;
using Optio.Queries;
using Optio.Streams;
using Optio.Telemetry;

namespace Optio.Endpoints;

/// <summary>
/// The stream of spec/05-stream.md. Each open stream belongs to a member of the guild in
/// <c>active_guild</c>. It has its own reader on the broker, and gets the events that the member
/// can read, and the query messages of its client (spec/06-query.md). The stream first sends its
/// position, then the state of the open query of the client, then the messages after the
/// position. It sends a comment at least each <see cref="KeepAlive"/>, and it ends when the access
/// token expires.
/// </summary>
internal static partial class StreamEndpoint
{
    public const string Path = "/api/v1/stream";
    public const string Source = "optio";
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>The wait for a client with too many open streams.</summary>
    public const int RetryAfterSeconds = 60;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapStream(this IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetService<EventStream>() is not null)
            app.MapGet(Path, GetAsync).RequireAuthorization(Authentication.CustosPolicy);
        return app;
    }

    private static async Task GetAsync(
        HttpContext http,
        EventStream events,
        OpenStreams open,
        IMemberDirectory members,
        OptioMetrics metrics,
        IOptions<OptioOptions> options,
        IHostEnvironment environment,
        TimeProvider time,
        ILogger<EventStream> logger,
        CancellationToken cancellationToken
    )
    {
        var user = http.User;
        if (
            TokenClaims.GuildId(user, options.Value, environment) is not { } guild
            || TokenClaims.DiscordUserId(user, options.Value, environment) is not { } userId
        )
        {
            await Forbidden("The token names no Discord guild or user.").ExecuteAsync(http);
            return;
        }
        if (!events.IsReady)
        {
            await BrokerUnavailable().ExecuteAsync(http);
            return;
        }

        var state = await members.ConnectAsync(guild, userId, cancellationToken);
        try
        {
            if (state is MemberState.NotMember)
            {
                await Forbidden("The user is not a member of the guild.").ExecuteAsync(http);
                return;
            }
            if (state is MemberState.Unavailable)
            {
                LogDiscordUnavailable(logger, guild);
                await Problem(
                        StatusCodes.Status503ServiceUnavailable,
                        "Service unavailable",
                        "Discord did not answer in time. Try again later."
                    )
                    .ExecuteAsync(http);
                return;
            }
            if (!open.TryOpen(userId))
            {
                http.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(
                    CultureInfo.InvariantCulture
                );
                await Problem(
                        StatusCodes.Status429TooManyRequests,
                        "Too many streams",
                        "The user has too many open streams."
                    )
                    .ExecuteAsync(http);
                return;
            }

            try
            {
                if (await events.NowAsync(cancellationToken) is not { } now)
                {
                    await BrokerUnavailable().ExecuteAsync(http);
                    return;
                }
                var start = Position.Start(
                    http.Request.Headers["Last-Event-ID"].FirstOrDefault(),
                    now
                );
                // The query messages of the client, when Optio has the query.
                var queries = http.RequestServices.GetService<QueryService>();
                var client = queries is null ? null : QueryClient.Of(user, guild, userId);
                await SendAsync(
                    http,
                    new Reader(
                        events,
                        members,
                        metrics,
                        time,
                        guild,
                        userId,
                        queries,
                        client,
                        logger
                    ),
                    start,
                    state,
                    Expiry(user),
                    cancellationToken
                );
            }
            finally
            {
                open.Close(userId);
            }
        }
        finally
        {
            members.Disconnect(guild, userId);
        }
    }

    private static async Task SendAsync(
        HttpContext http,
        Reader reader,
        Position start,
        MemberState state,
        DateTimeOffset expiry,
        CancellationToken cancellationToken
    )
    {
        var response = http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // The stream ends when the access token expires.
        using var expired = new CancellationTokenSource(
            TimeSpan.FromTicks(Math.Max(0, (expiry - reader.Time.GetUtcNow()).Ticks)),
            reader.Time
        );
        using var open = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            expired.Token
        );
        try
        {
            await response.StartAsync(open.Token);
            // The position first, so that a client with no event also has one.
            await WriteAsync(response, start, (byte[]?)null, open.Token);
            if (state is MemberState.NoBot)
            {
                await WriteAsync(
                    response,
                    start,
                    new StreamMessage(
                        "error",
                        "The Discord bot of this service is not in your guild. An administrator "
                            + "of the guild must add it.",
                        null,
                        Source
                    ),
                    open.Token
                );
            }

            if (await reader.OpenQueryAsync(open.Token) is { } openQuery)
                await WriteAsync(response, start, openQuery, open.Token);

            var events = reader.Events.Read(reader.Filters, start, open.Token);
            // The comment comes 15 seconds after the last write, also while the reader gets only
            // events that the member cannot read.
            var tick = new CancellationTokenSource(KeepAlive, reader.Time);
            try
            {
                while (true)
                {
                    while (events.TryRead(out var item))
                    {
                        var position = start with { Sequence = item.Sequence };
                        if (item.IsQuery)
                        {
                            // Optio made the message, for this client only.
                            await WriteAsync(response, position, item.Data, open.Token);
                        }
                        else
                        {
                            var producerEvent = ProducerEvent.Parse(
                                item.Subject,
                                item.Sequence,
                                item.Data
                            );
                            if (await reader.CanReadAsync(producerEvent, open.Token) is not { } e)
                                continue;
                            await WriteAsync(
                                response,
                                position,
                                new StreamMessage("event", e.Text, e.Reference, e.Source),
                                open.Token
                            );
                        }
                        tick.Dispose();
                        tick = new CancellationTokenSource(KeepAlive, reader.Time);
                    }

                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                        open.Token,
                        tick.Token
                    );
                    try
                    {
                        // False when the reader failed, for example when the stream went. The
                        // client then connects again, and gets a new position.
                        if (!await events.WaitToReadAsync(wait.Token))
                            return;
                    }
                    catch (OperationCanceledException) when (!open.IsCancellationRequested)
                    {
                        await response.WriteAsync(": keep-alive\n\n", open.Token);
                        await response.Body.FlushAsync(open.Token);
                        tick.Dispose();
                        tick = new CancellationTokenSource(KeepAlive, reader.Time);
                    }
                }
            }
            finally
            {
                tick.Dispose();
            }
        }
        catch (OperationCanceledException) when (open.IsCancellationRequested)
        {
            // The client closed the stream, or the token expired.
        }
    }

    /// <summary>Writes one event. With no message, the event only sets the position.</summary>
    private static Task WriteAsync(
        HttpResponse response,
        Position position,
        StreamMessage? message,
        CancellationToken cancellationToken
    ) =>
        WriteAsync(
            response,
            position,
            message is null ? null : JsonSerializer.SerializeToUtf8Bytes(message, Json),
            cancellationToken
        );

    /// <summary>Writes one event with a message as one line of JSON.</summary>
    private static async Task WriteAsync(
        HttpResponse response,
        Position position,
        byte[]? message,
        CancellationToken cancellationToken
    )
    {
        var data = message is null ? "" : $"data: {Encoding.UTF8.GetString(message)}\n";
        await response.WriteAsync($"id: {position}\n{data}\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static IResult BrokerUnavailable() =>
        Problem(
            StatusCodes.Status503ServiceUnavailable,
            "Service unavailable",
            "The events are not available now. Try again later."
        );

    /// <summary>The expiry of the token in the claim <c>exp</c>. The token handler requires
    /// it.</summary>
    private static DateTimeOffset Expiry(System.Security.Claims.ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst("exp")?.Value, CultureInfo.InvariantCulture, out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp)
            : DateTimeOffset.MinValue;

    private static IResult Forbidden(string detail) =>
        Problem(StatusCodes.Status403Forbidden, "Forbidden", detail);

    private static IResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);

    /// <summary>
    /// The reader of one client stream: the events of its guild, with the policy of now. It
    /// counts each event.
    /// </summary>
    private sealed class Reader(
        EventStream events,
        IMemberDirectory members,
        OptioMetrics metrics,
        TimeProvider time,
        ulong guild,
        ulong user,
        QueryService? queries,
        QueryClient? client,
        ILogger logger
    )
    {
        public EventStream Events => events;

        /// <summary>The filters of the broker: the events of the guild, and the query messages
        /// of the client.</summary>
        public string[] Filters =>
            client is null
                ? [ProducerEvent.Filter(guild)]
                : [ProducerEvent.Filter(guild), client.Subject];

        /// <summary>The message of the open query of the client. A failure only goes to the log:
        /// the stream then has the replay.</summary>
        public async Task<byte[]?> OpenQueryAsync(CancellationToken cancellationToken)
        {
            if (queries is null || client is null)
                return null;
            try
            {
                return (await queries.OpenMessageAsync(client, cancellationToken))?.ToStream();
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                LogNoOpenQuery(logger, e);
                return null;
            }
        }

        public TimeProvider Time => time;

        public ulong Guild => guild;

        /// <summary>The event when the member can read it. While the guild is not ready, it
        /// waits.</summary>
        public async Task<ProducerEvent?> CanReadAsync(
            ProducerEvent? producerEvent,
            CancellationToken cancellationToken
        )
        {
            if (producerEvent is null)
            {
                metrics.Event("invalid");
                return null;
            }
            if (!members.HasGuild(guild))
            {
                metrics.Event("no_bot");
                return null;
            }

            var policy = producerEvent.Policy;
            while (members.CanRead(policy, guild, user) is null)
                await GuildReadyAsync(cancellationToken);
            if (members.CanRead(policy, guild, user) is not true)
            {
                metrics.Event("denied");
                return null;
            }
            metrics.Event("sent");
            metrics.EventAgeOf(producerEvent.Source, time.GetUtcNow() - producerEvent.Time);
            return producerEvent;
        }

        private async Task GuildReadyAsync(CancellationToken cancellationToken)
        {
            var ready = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            void OnReady(ulong readyGuild)
            {
                if (readyGuild == guild)
                    ready.TrySetResult();
            }
            members.GuildReady += OnReady;
            try
            {
                // The guild can become ready before the handler is in place.
                if (members.CanRead(new EventPolicy(guild, [], null), guild, user) is null)
                    await ready.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                members.GuildReady -= OnReady;
            }
        }
    }

    /// <summary>A message of spec/05-stream.md.</summary>
    private sealed record StreamMessage(
        string Kind,
        string? Text,
        string? Reference,
        string Source
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Discord did not answer in time for a stream of guild {GuildId}"
    )]
    private static partial void LogDiscordUnavailable(ILogger logger, ulong guildId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The state of the open query was not available for a stream"
    )]
    private static partial void LogNoOpenQuery(ILogger logger, Exception exception);
}
