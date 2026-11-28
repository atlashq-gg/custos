using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Optio.Queries;

namespace Optio.Endpoints;

/// <summary>
/// The query of spec/06-query.md. A body with <c>text</c> is a query, a body with <c>action</c>
/// is an action. A query belongs to the client of the claim <c>sid</c>, in the guild of
/// <c>active_guild</c>.
/// </summary>
internal static partial class QueryEndpoint
{
    public const string Path = "/api/v1/query";
    public const int MaxText = 500;

    private static readonly string[] Actions = ["accept", "refuse", "stop"];

    public static IEndpointRouteBuilder MapQuery(this IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetService<QueryService>() is not null)
            app.MapPost(Path, PostAsync).RequireAuthorization(Authentication.CustosPolicy);
        return app;
    }

    private static async Task<IResult> PostAsync(
        HttpContext http,
        QueryService queries,
        IOptions<OptioOptions> options,
        IHostEnvironment environment,
        CancellationToken cancellationToken
    )
    {
        var user = http.User;
        if (
            TokenClaims.GuildId(user, options.Value, environment) is not { } guild
            || TokenClaims.DiscordUserId(user, options.Value, environment) is not { } userId
            || QueryClient.Of(user, guild, userId) is not { } client
        )
        {
            return Problem(
                StatusCodes.Status403Forbidden,
                "Forbidden",
                "The token names no Discord guild, user, or session."
            );
        }

        if (await ReadAsync(http.Request, cancellationToken) is not { } body)
            return Problem(
                StatusCodes.Status400BadRequest,
                "Bad request",
                "The body is not valid."
            );
        if (!queries.IsReady)
        {
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Service unavailable",
                "The query is not available now. Try again later."
            );
        }

        // The token of the request: an accepted change runs with the token of its decision.
        var token = await http.GetTokenAsync("access_token") ?? "";
        var outcome = body.Action is { } action
            ? await queries.ActAsync(
                client,
                body.Reference,
                action,
                token,
                Expiry(user),
                cancellationToken
            )
            : await queries.SubmitAsync(
                client,
                new QueryRequest(body.Reference, body.Text!, body.NewConversation),
                token,
                Expiry(user),
                cancellationToken
            );

        if (outcome.Problem is null && outcome.Record is { } record)
            return Results.Json(
                record.Message().ToState(),
                QueryMessage.Json,
                statusCode: outcome.Status
            );
        if (outcome.RetryAfter is { } wait)
        {
            http.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
        }
        return Problem(outcome.Status, Title(outcome.Status), outcome.Problem ?? "");
    }

    /// <summary>The body of a query or an action, or null when it is not valid
    /// (spec/schemas/query.schema.json, spec/schemas/query-action.schema.json).</summary>
    internal static async Task<Body?> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: cancellationToken
            );
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("reference", out var reference)
                || reference.GetString() is not { } referenceText
                || !Reference().IsMatch(referenceText)
            )
            {
                return null;
            }

            var hasText = root.TryGetProperty("text", out var text);
            var hasAction = root.TryGetProperty("action", out var action);
            if (hasText == hasAction)
                return null;
            if (hasAction)
            {
                return action.GetString() is { } a && Actions.Contains(a)
                    ? new Body(referenceText, null, a, false)
                    : null;
            }

            var newConversation = false;
            if (root.TryGetProperty("new_conversation", out var fresh))
                newConversation = fresh.GetBoolean();
            return text.GetString() is { Length: > 0 and <= MaxText } t
                ? new Body(referenceText, t, null, newConversation)
                : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal sealed record Body(
        string Reference,
        string? Text,
        string? Action,
        bool NewConversation
    );

    /// <summary>The expiry of the token in the claim <c>exp</c>.</summary>
    private static DateTimeOffset Expiry(System.Security.Claims.ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst("exp")?.Value, CultureInfo.InvariantCulture, out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp)
            : DateTimeOffset.MinValue;

    private static string Title(int status) =>
        status switch
        {
            StatusCodes.Status403Forbidden => "Forbidden",
            StatusCodes.Status404NotFound => "Not found",
            StatusCodes.Status409Conflict => "Conflict",
            StatusCodes.Status429TooManyRequests => "Limit reached",
            StatusCodes.Status503ServiceUnavailable => "Not available",
            _ => "Error",
        };

    private static IResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex Reference();
}
