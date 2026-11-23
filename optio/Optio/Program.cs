using System.Text.Json;
using Optio;
using Optio.Endpoints;
using Optio.Telemetry;

if (await CommandLine.RunAsync(args, Console.Out) is { } exitCode)
    return exitCode;

var builder = WebApplication.CreateBuilder(args);

// Docker secrets: the file /run/secrets/Optio__ClientSecret gives Optio:ClientSecret.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

// spec/ uses snake_case for JSON members.
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddProblemDetails();
builder.Services.AddOptioHealthChecks();
builder.AddOptioTelemetry();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealth();

LogStarting(app.Logger, OptioVersion.Value);

await app.RunAsync();
return 0;

/// <summary>The entry point. Public for the tests.</summary>
public partial class Program
{
    [LoggerMessage(Level = LogLevel.Information, Message = "optio {Version}")]
    private static partial void LogStarting(ILogger logger, string version);
}
