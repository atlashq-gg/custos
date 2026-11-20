using System.Diagnostics;
using System.Security.Cryptography;
using Custos.Server.MapData;
using Custos.Server.Storage;
using Custos.Server.Telemetry;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Custos.Server.Endpoints;

/// <summary>The upload result document (spec/03-upload.md). Unstable in contract version
/// v0.</summary>
public sealed record UploadResult(
    string Status,
    DateTimeOffset ReceivedAt,
    long SizeBytes,
    string Sha256,
    WarInfo? War,
    StockpileSummary[] Stockpiles
);

/// <summary>The war of the upload, as the upload result gives it.</summary>
public sealed record WarInfo(string WarId, int WarNumber);

public sealed record StockpileSummary(string Name, string? Location, int? ItemCount);

internal static class Uploads
{
    public const string Path = "/api/v1/uploads";
    public const string OctetStream = "application/octet-stream";

    public static IEndpointRouteBuilder MapUploads(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, PostAsync)
            .WithName("UploadMapData")
            .WithTags("Uploads")
            .WithSummary("Upload the full map data file of Foxhole.")
            .Accepts<Stream>(OctetStream)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(Authentication.CustosPolicy)
            .RequireRateLimiting(RateLimiting.UploadPolicy)
            .AddEndpointFilter(CountAsync);

        return app;
    }

    /// <summary>Counts each upload that the handler answers, by outcome.</summary>
    private static async ValueTask<object?> CountAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var metrics = context.HttpContext.RequestServices.GetRequiredService<ServerMetrics>();
        try
        {
            var result = await next(context);
            var status = (result as INestedHttpResult)?.Result ?? result;
            metrics.Upload(
                (status as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK
            );
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            metrics.Upload(StatusCodes.Status500InternalServerError);
            throw;
        }
    }

    private static async Task<Results<Ok<UploadResult>, ProblemHttpResult>> PostAsync(
        HttpRequest request,
        IOptions<CustosOptions> options,
        IHostEnvironment environment,
        WarApiClient warApi,
        WarRegistry wars,
        UploadStore store,
        TimeProvider time,
        ActivitySource activities,
        ServerMetrics metrics,
        CancellationToken cancellationToken
    )
    {
        var receivedAt = time.GetUtcNow();
        var guildId = ActiveGuild.Resolve(request.HttpContext.User, options.Value, environment);
        if (guildId is null)
        {
            return Problem(
                StatusCodes.Status403Forbidden,
                "No guild",
                $"The access token has no valid claim {ActiveGuild.ClaimType}. "
                    + "Choose a guild when you log in."
            );
        }

        var maxBytes = options.Value.UploadMaxBytes;

        if (
            !MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals(OctetStream, StringComparison.OrdinalIgnoreCase)
        )
        {
            return Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported media type",
                $"The body must be {OctetStream}."
            );
        }

        if (request.ContentLength > maxBytes)
            return TooLarge(maxBytes);

        // One byte more than the limit, so that a body without Content-Length can be found too
        // large.
        var body = new MemoryStream();
        var limited = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limited is { IsReadOnly: false })
            limited.MaxRequestBodySize = maxBytes + 1L;

        try
        {
            await request.Body.CopyToAsync(body, cancellationToken);
        }
        catch (BadHttpRequestException e)
            when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(maxBytes);
        }

        if (body.Length > maxBytes)
            return TooLarge(maxBytes);

        var bytes = body.GetBuffer().AsMemory(0, (int)body.Length);
        IReadOnlyList<PinnedItem> items;
        using (var activity = activities.StartActivity("Parse map data"))
        {
            var started = time.GetTimestamp();
            try
            {
                items = MapDataFile.Parse(bytes);
                activity?.SetTag("custos.pinned_items", items.Count);
            }
            catch (GvasFormatException e)
            {
                activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                return Problem(
                    StatusCodes.Status422UnprocessableEntity,
                    "Not a map data file",
                    e.Message
                );
            }
            finally
            {
                metrics.UploadParsed(time.GetElapsedTime(started));
            }
        }

        // The key of an entity needs the war, and the server does not keep the file. Thus Custos
        // tries again.
        var war = await warApi.GetCurrentWarAsync(cancellationToken);
        if (war is null)
        {
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                "War unknown",
                "The Foxhole war API did not answer. Try again later."
            );
        }

        var warStartedAt = await wars.RecordAsync(war, cancellationToken);
        var upload = new Upload
        {
            GuildId = guildId.Value,
            WarId = war.WarId,
            UploadId = Guid.CreateVersion7(receivedAt),
            UserId = request.HttpContext.User.FindFirst("sub")?.Value ?? "",
            ReceivedAt = receivedAt,
            Sha256 = SHA256.HashData(bytes.Span),
            SizeBytes = bytes.Length,
        };
        var stockpiles = await store.SaveAsync(upload, items, warStartedAt, cancellationToken);

        return TypedResults.Ok(
            new UploadResult(
                "accepted",
                receivedAt,
                upload.SizeBytes,
                Convert.ToHexStringLower(upload.Sha256),
                new WarInfo(war.WarId.ToString("D"), war.WarNumber),
                stockpiles
            )
        );
    }

    private static ProblemHttpResult TooLarge(int maxBytes) =>
        Problem(
            StatusCodes.Status413PayloadTooLarge,
            "File too large",
            $"The body is larger than {maxBytes} bytes."
        );

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
