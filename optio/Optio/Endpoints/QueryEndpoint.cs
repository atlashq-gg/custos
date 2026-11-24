using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Optio.Harness;
using Optio.Streams;

namespace Optio.Endpoints;

/// <summary>The answer of <see cref="QueryEndpoint"/> (spec/schemas/query-accepted.schema.json).
/// </summary>
public sealed record QueryAccepted(string Reference);

/// <summary>
/// The query of spec/05-stream.md. optio accepts the query, and the harness answers on the open
/// streams of the user. The answer to a confirmation comes with M34: until then, no confirmation
/// is open.
/// </summary>
internal static class QueryEndpoint
{
    public const string Path = "/api/v1/query";
    public const int MaxTextLength = 500;

    public static IEndpointRouteBuilder MapQuery(this IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetService<QueryLimiter>() is not null)
            app.MapPost(Path, PostAsync).RequireAuthorization(Authentication.CustosPolicy);
        return app;
    }

    private static async Task<IResult> PostAsync(
        HttpContext http,
        StreamHub hub,
        QueryLimiter limiter,
        IServiceScopeFactory scopes,
        IHostApplicationLifetime lifetime,
        IOptions<OptioOptions> options,
        IHostEnvironment environment
    )
    {
        string text;
        try
        {
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            var root = body.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("confirm", out _))
                return Problem(StatusCodes.Status409Conflict, "The confirmation is not open.");
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("text", out var value)
                || value.ValueKind != JsonValueKind.String
                || value.GetString() is not { Length: > 0 and <= MaxTextLength } query
            )
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    $"The body needs text with 1 to {MaxTextLength} characters."
                );
            }
            text = query;
        }
        catch (JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The body is not JSON.");
        }

        if (
            TokenClaims.GuildId(http.User, options.Value, environment) is not { } guild
            || TokenClaims.DiscordUserId(http.User, options.Value, environment) is not { } user
        )
        {
            return Problem(StatusCodes.Status403Forbidden, "The token names no guild or user.");
        }
        if (!hub.HasStream(guild, user))
            return Problem(StatusCodes.Status409Conflict, "The user has no open stream.");
        if (await http.GetTokenAsync("access_token") is not { } accessToken)
            return Problem(StatusCodes.Status403Forbidden, "The request has no token.");

        if (limiter.TryStart(user) is { } wait)
        {
            http.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
            return Problem(StatusCodes.Status429TooManyRequests, "Too many queries.");
        }

        var reference = $"query:{Guid.NewGuid():N}";
        var stopping = lifetime.ApplicationStopping;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope
                        .ServiceProvider.GetRequiredService<QueryHarness>()
                        .RunAsync(new Query(guild, user, accessToken, reference, text), stopping);
                }
                finally
                {
                    limiter.Finish(user);
                }
            },
            CancellationToken.None
        );
        return TypedResults.Accepted((string?)null, new QueryAccepted(reference));
    }

    private static IResult Problem(int status, string detail) =>
        TypedResults.Problem(statusCode: status, detail: detail);
}
