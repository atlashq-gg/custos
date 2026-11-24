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
/// streams of the client that sent it. The same endpoint takes the answer of that client to a
/// confirmation.
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
        Confirmations confirmations,
        IServiceScopeFactory scopes,
        IHostApplicationLifetime lifetime,
        IOptions<OptioOptions> options,
        IHostEnvironment environment
    )
    {
        string? text = null;
        (string Reference, bool Accept)? answer = null;
        try
        {
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Problem(StatusCodes.Status400BadRequest, "The body is not a JSON object.");
            if (root.TryGetProperty("confirm", out var confirm))
            {
                if (
                    confirm.GetString() is not { Length: > 0 } reference
                    || !root.TryGetProperty("accept", out var accept)
                    || accept.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                )
                {
                    return Problem(
                        StatusCodes.Status400BadRequest,
                        "The answer needs confirm and accept."
                    );
                }
                answer = (reference, accept.GetBoolean());
            }
            else if (
                root.TryGetProperty("text", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 and <= MaxTextLength } query
            )
            {
                text = query;
            }
            else
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    $"The body needs text with 1 to {MaxTextLength} characters."
                );
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The body is not valid.");
        }

        if (TokenClaims.Client(http.User, options.Value, environment) is not { } client)
            return Problem(StatusCodes.Status403Forbidden, "The token names no guild or user.");
        if (!hub.HasStream(client))
            return Problem(StatusCodes.Status409Conflict, "The user has no open stream.");
        if (await http.GetTokenAsync("access_token") is not { } accessToken)
            return Problem(StatusCodes.Status403Forbidden, "The request has no token.");

        if (answer is var (confirmReference, accepted))
        {
            if (confirmations.Take(client, confirmReference) is not { } confirmation)
                return Problem(StatusCodes.Status409Conflict, "The confirmation is not open.");
            if (accepted)
            {
                Run(
                    scopes,
                    lifetime,
                    (harness, stopping) =>
                        harness.ConfirmAsync(confirmation, accessToken, stopping),
                    () => { }
                );
            }
            else
            {
                Remove(hub, client, confirmReference);
            }
            return TypedResults.Accepted((string?)null, new QueryAccepted(confirmReference));
        }

        if (limiter.TryStart(client.User) is { } wait)
        {
            http.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
            return Problem(StatusCodes.Status429TooManyRequests, "Too many queries.");
        }
        // A new query of the client ends its open confirmation.
        if (confirmations.Cancel(client) is { } cancelled)
            Remove(hub, client, cancelled);

        var queryReference = $"query:{Guid.NewGuid():N}";
        Run(
            scopes,
            lifetime,
            (harness, stopping) =>
                harness.RunAsync(new Query(client, accessToken, queryReference, text!), stopping),
            () => limiter.Finish(client.User)
        );
        return TypedResults.Accepted((string?)null, new QueryAccepted(queryReference));
    }

    /// <summary>Runs the harness in the background, in its own scope.</summary>
    private static void Run(
        IServiceScopeFactory scopes,
        IHostApplicationLifetime lifetime,
        Func<QueryHarness, CancellationToken, Task> work,
        Action done
    )
    {
        var stopping = lifetime.ApplicationStopping;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await work(scope.ServiceProvider.GetRequiredService<QueryHarness>(), stopping);
                }
                finally
                {
                    done();
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>Removes the line of a confirmation: a message with its reference and no text.
    /// </summary>
    private static void Remove(StreamHub hub, StreamClient client, string reference) =>
        hub.SendToClient(
            client,
            new StreamMessage("confirm", null, reference, QueryHarness.Source)
        );

    private static IResult Problem(int status, string detail) =>
        TypedResults.Problem(statusCode: status, detail: detail);
}
