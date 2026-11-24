using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Optio.Tokens;

/// <summary>
/// The token exchange of RFC 8693 as the client optio: a token of Custos for a token of a domain
/// service. The scope selects the service, because the scope of a service adds its audience. The
/// cache keeps a token until 30 seconds before it expires. Its key holds a hash of the token of
/// Custos, not the token.
/// </summary>
internal sealed partial class TokenExchange(
    HttpClient http,
    IOptions<OptioOptions> options,
    IOptionsMonitor<JwtBearerOptions> jwt,
    IMemoryCache cache,
    ILogger<TokenExchange> logger
)
{
    private const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// A token for the scope <paramref name="scope"/>, or <c>null</c> when the authorization
    /// server refuses the exchange. A failure of the connection throws.
    /// </summary>
    public async Task<string?> ExchangeAsync(
        string subjectToken,
        string scope,
        CancellationToken cancellationToken
    )
    {
        var key = CacheKey(subjectToken, scope);
        if (cache.TryGetValue<string>(key, out var cached))
            return cached;

        var o = options.Value;
        var metadata = await jwt.Get(JwtBearerDefaults.AuthenticationScheme)
            .ConfigurationManager!.GetConfigurationAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = GrantType,
                    ["subject_token"] = subjectToken,
                    ["subject_token_type"] = AccessTokenType,
                    ["scope"] = scope,
                }
            ),
        };
        // RFC 6749 section 2.3.1: the client ID and the secret are form-encoded before Base64.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    $"{Uri.EscapeDataString(o.OptioClientId)}:"
                        + Uri.EscapeDataString(o.OptioClientSecret)
                )
            )
        );

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogRefused(logger, scope, (int)response.StatusCode);
            return null;
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(
            Json,
            cancellationToken
        );
        if (token?.AccessToken is not { Length: > 0 } accessToken)
        {
            LogRefused(logger, scope, (int)response.StatusCode);
            return null;
        }

        var lifetime = TimeSpan.FromSeconds(token.ExpiresIn) - Margin;
        if (lifetime > TimeSpan.Zero)
            cache.Set(key, accessToken, lifetime);
        return accessToken;
    }

    private static string CacheKey(string subjectToken, string scope)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subjectToken)));
        return $"token-exchange:{hash}:{scope}";
    }

    private sealed record TokenResponse(string? AccessToken, int ExpiresIn);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The authorization server refused the token exchange for {Scope} with {Status}."
    )]
    private static partial void LogRefused(ILogger logger, string scope, int status);
}
