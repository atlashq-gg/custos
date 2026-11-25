using Custos.Server.Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Custos.Server;

/// <summary>
/// Validates the access tokens of the upload API. Optio exchanges the token of Custos for a token
/// of this server (RFC 8693), so the token has the audience <see cref="CustosOptions.Resource"/>.
/// The upload needs the write scope. The Discord roles of the member decide the rest.
/// </summary>
internal static class Authentication
{
    public const string UploadPolicy = "upload";

    public static IHostApplicationBuilder AddCustosAuthentication(
        this IHostApplicationBuilder builder
    )
    {
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        builder
            .Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<CustosOptions>>(
                (jwt, custos) =>
                {
                    var authority = custos.Value.Authority;
                    var resource = custos.Value.Resource;

                    jwt.Authority = authority;
                    jwt.RequireHttpsMetadata = new Uri(authority).Scheme == Uri.UriSchemeHttps;
                    jwt.MapInboundClaims = false;
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = authority,
                        ValidateIssuer = true,
                        // Some authorization servers write the resource with a slash at the end.
                        ValidAudiences = [resource, resource + "/"],
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        NameClaimType = "sub",
                    };
                }
            );

        builder
            .Services.AddAuthorizationBuilder()
            .AddPolicy(UploadPolicy, policy =>
                policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        context.Resource is HttpContext http
                        && McpAccessFilter.HasScope(
                            context.User,
                            http.RequestServices.GetRequiredService<
                                IOptions<McpOptions>
                            >().Value.WriteScope
                        )
                    )
            );

        return builder;
    }
}
