using System.Text.Json;
using Optio;
using Optio.Discord;
using Optio.Endpoints;
using Optio.Queries;
using Optio.Storage;
using Optio.Streams;
using Optio.Telemetry;
using Optio.Tokens;

if (await CommandLine.RunAsync(args, Console.Out) is { } exitCode)
    return exitCode;

var builder = WebApplication.CreateBuilder(args);

// Docker secrets: the file /run/secrets/Optio__OptioClientSecret gives Optio:OptioClientSecret.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

builder
    .Services.AddOptions<OptioOptions>()
    .BindConfiguration(OptioOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// spec/ uses snake_case for JSON members.
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OptioMetrics>();
builder.Services.AddOptioHealthChecks();
builder.Services.AddHttpClient<TokenExchange>().AddStandardResilienceHandler();
builder.Services.AddHttpClient<UploadRelay>(http => http.Timeout = TimeSpan.FromSeconds(60));

// The storage first: its migrator is the first hosted service.
builder
    .AddOptioTelemetry()
    .AddOptioAuthentication()
    .AddOptioStorage()
    .AddOptioDiscord()
    .AddOptioStream()
    .AddOptioQuery();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapDiscovery();
app.MapClientMetadata();
app.MapUploads();
app.MapStream();
app.MapQuery();
app.MapHealth();

LogStarting(app.Logger, OptioVersion.Value);

await app.RunAsync();
return 0;

/// <summary>The entry point. Public for the tests.</summary>
public partial class Program
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Optio {Version}")]
    private static partial void LogStarting(ILogger logger, string version);
}
