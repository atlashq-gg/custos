using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Custos.Server;

/// <summary>
/// The OpenAPI document that the server generates from its code. spec/ stays the authority for the
/// contract.
/// </summary>
internal static class OpenApiSetup
{
    public const string DocumentName = "v1";
    public const string DocumentPath = "/openapi/{documentName}.json";
    public const string ScalarPath = "/docs";
    private const string BearerScheme = "Bearer";

    public static IHostApplicationBuilder AddCustosOpenApi(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.AddDocumentTransformer(
                (document, _, _) =>
                {
                    document.Info.Title = "Custos reference server";
                    document.Info.Description =
                        "Generated from the code. "
                        + "The contract is defined in spec/ of the Custos repository.";
                    document.Components ??= new OpenApiComponents();
                    document.Components.SecuritySchemes ??=
                        new Dictionary<string, IOpenApiSecurityScheme>();
                    document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description =
                            "An access token from the authorization server "
                            + "in the discovery document.",
                    };
                    return Task.CompletedTask;
                }
            );
        });

        return builder;
    }

    public static WebApplication MapCustosOpenApi(this WebApplication app)
    {
        app.MapOpenApi(DocumentPath);
        app.MapScalarApiReference(ScalarPath, options =>
            options.WithOpenApiRoutePattern(DocumentPath).AddPreferredSecuritySchemes(BearerScheme)
        );

        return app;
    }
}
