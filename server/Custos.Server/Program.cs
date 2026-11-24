using System.Text.Json;
using Custos.Server;
using Custos.Server.Catalog;
using Custos.Server.Discord;
using Custos.Server.Endpoints;
using Custos.Server.Mcp;
using Custos.Server.Storage;
using Custos.Server.Telemetry;

if (await CommandLine.RunAsync(args, Console.Out) is { } exitCode)
    return exitCode;

var builder = WebApplication.CreateBuilder(args);

// Docker secrets: the file /run/secrets/Discord__Token gives Discord:Token.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

builder
    .Services.AddOptions<CustosOptions>()
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
builder.Services.AddCustosHealthChecks();
builder
    .Services.AddHttpClient<WarApiClient>()
    .AddStandardResilienceHandler(WarApiClient.Resilience);
builder.Services.AddSingleton(TimeProvider.System);

// No retries: a retrying execution strategy refuses the transactions that the server opens itself.
// Custos sends the file again at the next interval after a 5xx. The health check of the database
// is in AddCustosHealthChecks, with its tag.
builder.AddNpgsqlDbContext<CustosDbContext>(
    CustosDbContext.ConnectionName,
    settings =>
    {
        settings.DisableRetry = true;
        settings.DisableHealthChecks = true;
    },
    CustosDbContext.Configure
);
builder.Services.AddScoped<WarRegistry>();
builder.Services.AddScoped<UploadStore>();
builder.Services.AddScoped<WarMaps>();
builder.Services.AddScoped<StructureLabels>();
builder.Services.AddScoped<GuildRecords>();

// The migrator must start first: hosted services start in the order of registration.
builder.Services.AddHostedService<DatabaseMigrator>();
builder.Services.AddHostedService<LabelBackfill>();
builder.Services.AddHostedService<WarMonitor>();

builder
    .AddCustosTelemetry()
    .AddCustosCatalog()
    .AddCustosDiscord()
    .AddCustosAuthentication()
    .AddCustosMcp()
    .AddCustosRateLimiting()
    .AddCustosOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapUploads();
app.MapCustosOpenApi();
app.MapHealth();
app.MapCustosDiscord();
app.MapCustosMcp();

LogStarting(app.Logger, ServerVersion.Value);

await app.RunAsync();
return 0;

/// <summary>The entry point. Public for the tests.</summary>
public partial class Program
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Custos reference server {Version}")]
    private static partial void LogStarting(ILogger logger, string version);
}
