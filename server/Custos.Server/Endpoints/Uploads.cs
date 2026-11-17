using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Custos.Server.Endpoints;

/// <summary>The upload result document (spec/upload.md). Unstable in contract version 0.1.</summary>
public sealed record UploadResult(
    string Status,
    DateTimeOffset ReceivedAt,
    long SizeBytes,
    string Sha256,
    WarInfo? War,
    StockpileSummary[] Stockpiles);

public sealed record StockpileSummary(string Name, string? Location, int? ItemCount);

internal static class Uploads
{
    public const string Path = "/api/v1/uploads";
    public const string OctetStream = "application/octet-stream";

    /// <summary>The magic bytes of an Unreal Engine save file.</summary>
    private static ReadOnlySpan<byte> GvasMagic => "GVAS"u8;

    public static IEndpointRouteBuilder MapUploads(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, PostAsync)
            .WithName("UploadMapData")
            .WithTags("Uploads")
            .WithSummary("Upload the full map data file of Foxhole.")
            .Accepts<Stream>(OctetStream)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireAuthorization(Authentication.CustosPolicy)
            .RequireRateLimiting(RateLimiting.UploadPolicy);

        return app;
    }

    private static async Task<Results<Ok<UploadResult>, ProblemHttpResult>> PostAsync(
        HttpRequest request,
        IOptions<CustosOptions> options,
        WarApiClient warApi,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var maxBytes = options.Value.UploadMaxBytes;

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals(OctetStream, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Unsupported media type",
                $"The body must be {OctetStream}.");
        }

        if (request.ContentLength > maxBytes) return TooLarge(maxBytes);

        // One byte more than the limit, so that a body without Content-Length can be found too large.
        var body = new MemoryStream();
        var limited = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limited is { IsReadOnly: false }) limited.MaxRequestBodySize = maxBytes + 1L;

        try
        {
            await request.Body.CopyToAsync(body, cancellationToken);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(maxBytes);
        }

        if (body.Length > maxBytes) return TooLarge(maxBytes);

        var bytes = body.GetBuffer().AsSpan(0, (int)body.Length);
        if (!bytes.StartsWith(GvasMagic))
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Not a save file",
                "The body is not an Unreal Engine save file (GVAS).");
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var war = await warApi.GetCurrentWarAsync(cancellationToken);

        // A placeholder: this server does not parse the file, so the stockpile list is empty.
        return TypedResults.Ok(new UploadResult("accepted", time.GetUtcNow(), body.Length, sha256, war, []));
    }

    private static ProblemHttpResult TooLarge(int maxBytes) =>
        Problem(StatusCodes.Status413PayloadTooLarge, "File too large",
            $"The body is larger than {maxBytes} bytes.");

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
