using System.Text.Json;
using Custos.Server;
using Custos.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<CustosOptions>()
    .BindConfiguration(CustosOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// spec/ uses snake_case for JSON members.
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient<WarApiClient>(http => http.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton(TimeProvider.System);

builder
    .AddCustosAuthentication()
    .AddCustosRateLimiting()
    .AddCustosOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapDiscovery();
app.MapClientMetadata();
app.MapUploads();
app.MapCustosOpenApi();
app.MapHealthChecks("/health/alive");

app.Run();

/// <summary>The entry point. Public for the tests.</summary>
public partial class Program;
