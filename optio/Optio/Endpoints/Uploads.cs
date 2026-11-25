using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Optio.Tokens;

namespace Optio.Endpoints;

/// <summary>
/// The upload of spec/03-upload.md. Optio examines the token, the content type, and the size.
/// Then it sends the body as a stream to the upload API of the server, with a token of the user
/// for the server, and gives back the answer of the server with no change.
/// </summary>
internal static partial class Uploads
{
    public const string Path = "/api/v1/uploads";
    public const string OctetStream = "application/octet-stream";

    public static IEndpointRouteBuilder MapUploads(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, PostAsync).RequireAuthorization(Authentication.CustosPolicy);
        return app;
    }

    private static async Task PostAsync(
        HttpContext http,
        IOptions<OptioOptions> options,
        TokenExchange tokens,
        UploadRelay relay,
        ILogger<UploadRelay> logger,
        CancellationToken cancellationToken
    )
    {
        var request = http.Request;
        var maxBytes = options.Value.UploadMaxBytes;

        if (
            !MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals(OctetStream, StringComparison.OrdinalIgnoreCase)
        )
        {
            await Problem(
                    StatusCodes.Status415UnsupportedMediaType,
                    "Unsupported media type",
                    $"The body must be {OctetStream}."
                )
                .ExecuteAsync(http);
            return;
        }

        if (request.ContentLength > maxBytes)
        {
            await TooLarge(maxBytes).ExecuteAsync(http);
            return;
        }

        // One byte more than the limit, so that a body without Content-Length can be found too
        // large. The server examines the size of the body again.
        var limited = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limited is { IsReadOnly: false })
            limited.MaxRequestBodySize = maxBytes + 1L;

        try
        {
            var subject = await http.GetTokenAsync("access_token");
            var token = subject is null
                ? null
                : await tokens.ExchangeAsync(subject, options.Value.UploadScope, cancellationToken);
            if (token is null)
            {
                await Unavailable().ExecuteAsync(http);
                return;
            }

            using var response = await relay.SendAsync(
                request.Body,
                request.ContentLength,
                token,
                cancellationToken
            );
            await CopyAsync(response, http.Response, cancellationToken);
        }
        catch (Exception e) when (IsTooLarge(e))
        {
            await TooLarge(maxBytes).ExecuteAsync(http);
        }
        catch (Exception e)
            when (e is HttpRequestException
                || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            )
        {
            LogServerUnavailable(logger, e.GetType().Name);
            await Unavailable().ExecuteAsync(http);
        }
    }

    /// <summary>The status, the body, its type, and <c>Retry-After</c> of the server.</summary>
    private static async Task CopyAsync(
        HttpResponseMessage source,
        HttpResponse target,
        CancellationToken cancellationToken
    )
    {
        target.StatusCode = (int)source.StatusCode;
        if (source.Content.Headers.ContentType is { } contentType)
            target.ContentType = contentType.ToString();
        if (source.Headers.RetryAfter is { } retryAfter)
            target.Headers.RetryAfter = retryAfter.ToString();
        await source.Content.CopyToAsync(target.Body, cancellationToken);
    }

    private static bool IsTooLarge(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
                return true;
        }
        return false;
    }

    private static IResult TooLarge(int maxBytes) =>
        Problem(
            StatusCodes.Status413PayloadTooLarge,
            "Payload too large",
            $"The body must not be larger than {maxBytes} bytes."
        );

    private static IResult Unavailable() =>
        Problem(
            StatusCodes.Status503ServiceUnavailable,
            "Service unavailable",
            "The server of the guild records cannot take the file now. Try again later."
        );

    private static IResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The upload API of the server did not answer: {Error}."
    )]
    private static partial void LogServerUnavailable(ILogger logger, string error);
}

/// <summary>The upload API of the server. No retries: the body is a stream that is read
/// one time.</summary>
internal sealed class UploadRelay(HttpClient http, IOptions<OptioOptions> options)
{
    public async Task<HttpResponseMessage> SendAsync(
        Stream body,
        long? length,
        string token,
        CancellationToken cancellationToken
    )
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new(Uploads.OctetStream);
        content.Headers.ContentLength = length;
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.UploadApiUrl)
        {
            Content = content,
        };
        request.Headers.Authorization = new("Bearer", token);
        return await http.SendAsync(request, cancellationToken);
    }
}
