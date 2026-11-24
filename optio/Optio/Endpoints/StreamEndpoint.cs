using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Optio.Discord;
using Optio.Streams;

namespace Optio.Endpoints;

/// <summary>
/// The stream of spec/05-stream.md. Each open stream belongs to a member of the guild in
/// <c>active_guild</c>, and gets the events that the member can read. The stream sends a comment
/// at least each <see cref="KeepAlive"/>, and it ends when the access token expires.
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
        if (app.ServiceProvider.GetService<StreamHub>() is not null)
            app.MapGet(Path, GetAsync).RequireAuthorization(Authentication.CustosPolicy);
        return app;
    }

    private static async Task GetAsync(
        HttpContext http,
        StreamHub hub,
        IMemberDirectory members,
        IOptions<OptioOptions> options,
        IHostEnvironment environment,
        TimeProvider time,
        ILogger<StreamHub> logger,
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

            var lastEventId = http.Request.Headers["Last-Event-ID"].FirstOrDefault();
            if (hub.Subscribe(guild, userId, lastEventId) is not { } subscriber)
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
                await SendAsync(
                    http,
                    hub,
                    subscriber,
                    state,
                    Expiry(user),
                    time,
                    cancellationToken
                );
            }
            finally
            {
                hub.Unsubscribe(subscriber);
            }
        }
        finally
        {
            members.Disconnect(guild, userId);
        }
    }

    private static async Task SendAsync(
        HttpContext http,
        StreamHub hub,
        Subscriber subscriber,
        MemberState state,
        DateTimeOffset expiry,
        TimeProvider time,
        CancellationToken cancellationToken
    )
    {
        var response = http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // The stream ends when the access token expires.
        using var expired = new CancellationTokenSource(
            TimeSpan.FromTicks(Math.Max(0, (expiry - time.GetUtcNow()).Ticks)),
            time
        );
        using var open = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            expired.Token
        );
        try
        {
            await response.StartAsync(open.Token);
            await response.Body.FlushAsync(open.Token);
            if (state is MemberState.NoBot)
            {
                await WriteAsync(
                    response,
                    hub.CurrentId,
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

            while (true)
            {
                while (subscriber.Events.TryRead(out var streamEvent))
                {
                    var e = streamEvent.Event;
                    await WriteAsync(
                        response,
                        streamEvent.Id,
                        new StreamMessage("event", e.Text, e.Reference, e.Source),
                        open.Token
                    );
                }

                using var tick = new CancellationTokenSource(KeepAlive, time);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                    open.Token,
                    tick.Token
                );
                try
                {
                    await subscriber.Events.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!open.IsCancellationRequested)
                {
                    await response.WriteAsync(": keep-alive\n\n", open.Token);
                    await response.Body.FlushAsync(open.Token);
                }
            }
        }
        catch (OperationCanceledException) when (open.IsCancellationRequested)
        {
            // The client closed the stream, or the token expired.
        }
    }

    private static async Task WriteAsync(
        HttpResponse response,
        string id,
        StreamMessage message,
        CancellationToken cancellationToken
    )
    {
        var data = JsonSerializer.Serialize(message, Json);
        await response.WriteAsync($"id: {id}\ndata: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

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
}
