using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Optio;

/// <summary>
/// Validates the access tokens of Custos. The scope <c>custos:client</c> gives a token of Custos
/// the audience <see cref="OptioOptions.OptioClientId"/>, and the policy requires the client of
/// Custos in <c>azp</c>. The handler keeps the token for the token exchange.
/// </summary>
internal static class Authentication
{
    public const string CustosPolicy = "custos";

    public static IHostApplicationBuilder AddOptioAuthentication(
        this IHostApplicationBuilder builder
    )
    {
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        builder
            .Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<OptioOptions>>(
                (jwt, optio) =>
                {
                    var o = optio.Value;

                    jwt.Authority = o.Authority;
                    jwt.MetadataAddress = o.ResolvedOpenIdConfiguration;
                    jwt.RequireHttpsMetadata = new Uri(o.Authority).Scheme == Uri.UriSchemeHttps;
                    jwt.MapInboundClaims = false;
                    jwt.SaveToken = true;
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = o.Authority,
                        ValidateIssuer = true,
                        ValidAudience = o.OptioClientId,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        NameClaimType = "sub",
                    };
                }
            );

        builder
            .Services.AddAuthorizationBuilder()
            .AddPolicy(CustosPolicy, policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                    {
                        var clientId = context.Resource is HttpContext http
                            ? http
                                .RequestServices.GetRequiredService<IOptions<OptioOptions>>()
                                .Value.ResolvedCustosClientId
                            : null;
                        return clientId is not null && context.User.HasClaim("azp", clientId);
                    }));

        return builder;
    }
}
