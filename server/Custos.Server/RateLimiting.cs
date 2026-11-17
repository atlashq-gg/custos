using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Custos.Server;

/// <summary>
/// One upload for each user in each <see cref="CustosOptions.UploadMinIntervalSeconds"/>. A rejected request
/// gets <c>429</c> with <c>Retry-After</c> (spec/03-upload.md).
/// </summary>
internal static class RateLimiting
{
    public const string UploadPolicy = "upload";

    public static IHostApplicationBuilder AddCustosRateLimiting(this IHostApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                    ? (int)Math.Ceiling(wait.TotalSeconds)
                    : 1;
                context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

                await TypedResults.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many uploads",
                        detail: $"Send the next upload after {retryAfter} seconds.")
                    .ExecuteAsync(context.HttpContext);
            };

            limiter.AddPolicy(UploadPolicy, http =>
            {
                var interval = http.RequestServices.GetRequiredService<IOptions<CustosOptions>>().Value.UploadMinIntervalSeconds;
                var user = http.User.FindFirst("sub")?.Value ?? "";

                return interval == 0
                    ? RateLimitPartition.GetNoLimiter(user)
                    : RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 1,
                        Window = TimeSpan.FromSeconds(interval),
                        QueueLimit = 0,
                    });
            });
        });

        return builder;
    }
}
