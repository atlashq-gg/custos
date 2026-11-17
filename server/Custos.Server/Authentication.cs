using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Custos.Server;

/// <summary>
/// Validates the access tokens that the authorization server issues to Custos. A token for Custos has no
/// audience, so the policy requires the <c>azp</c> claim to be the client ID of Custos instead.
/// </summary>
internal static class Authentication
{
    public const string CustosPolicy = "custos";

    public static IHostApplicationBuilder AddCustosAuthentication(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        builder.Services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<CustosOptions>>((jwt, custos) =>
            {
                var authority = custos.Value.Authority;

                jwt.Authority = authority;
                jwt.RequireHttpsMetadata = new Uri(authority).Scheme == Uri.UriSchemeHttps;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = authority,
                    ValidateIssuer = true,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "sub",
                };
            });

        builder.Services
            .AddAuthorizationBuilder()
            .AddPolicy(CustosPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                {
                    var clientId = context.Resource is HttpContext http
                        ? http.RequestServices.GetRequiredService<IOptions<CustosOptions>>().Value.ResolvedClientId
                        : null;
                    return clientId is not null && context.User.HasClaim("azp", clientId);
                }));

        return builder;
    }
}
