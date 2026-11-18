using System.Text.Json;
using Custos.Server;
using Custos.Server.Endpoints;
using Custos.Server.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

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

// No retries: a retrying execution strategy refuses the transactions that the server opens itself. Custos
// sends the file again at the next interval after a 5xx.
builder.AddNpgsqlDbContext<CustosDbContext>(CustosDbContext.ConnectionName,
    settings => settings.DisableRetry = true,
    CustosDbContext.Configure);
builder.Services.AddScoped<WarRegistry>();
builder.Services.AddScoped<UploadStore>();
// The migrator must start first: hosted services start in the order of registration.
builder.Services.AddHostedService<DatabaseMigrator>();
builder.Services.AddHostedService<WarMonitor>();

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
// Liveness does not depend on the database. Readiness runs all checks, the database too.
app.MapHealthChecks("/health/alive", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

app.Run();

/// <summary>The entry point. Public for the tests.</summary>
public partial class Program;
