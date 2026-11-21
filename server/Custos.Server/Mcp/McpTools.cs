using System.Diagnostics;
using Custos.Server.Discord;
using Custos.Server.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Custos.Server.Mcp;

/// <summary>
/// The base of the tool classes. <see cref="RunAsync"/> writes the result as JSON text, measures
/// the call, and writes the audit line. Text from players goes into the values of JSON members
/// only, never into a tool description.
/// </summary>
public abstract partial class McpTools(McpCallContext context, IServiceProvider services)
{
    protected McpCallContext Context => context;

    public static CallToolResult Problem(McpProblem problem) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = problem.ToJson() }] };

    protected async Task<CallToolResult> RunAsync<T>(string tool, Func<Task<T>> body)
    {
        var activity = services.GetRequiredService<ActivitySource>().StartActivity($"mcp {tool}");
        var started = Stopwatch.GetTimestamp();
        var outcome = "error";
        try
        {
            var result = new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = System.Text.Json.JsonSerializer.Serialize(
                            await body(),
                            McpJson.Options
                        ),
                    },
                ],
            };
            outcome = "ok";
            return result;
        }
        catch (McpDeniedException e)
        {
            outcome = "denied";
            return Problem(e.Problem);
        }
        catch (McpException)
        {
            // A wrong argument. The SDK gives the message to the model.
            outcome = "invalid_argument";
            throw;
        }
        catch (DiscordUnavailableException)
        {
            outcome = "discord_unavailable";
            return Problem(McpProblem.DiscordUnavailable());
        }
        finally
        {
            if (outcome == "error")
                activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("custos.outcome", outcome);
            activity?.Dispose();
            Record(services, tool, context, outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>Writes the metrics and the audit line of one call.</summary>
    internal static void Record(
        IServiceProvider services,
        string tool,
        McpCallContext context,
        string outcome,
        TimeSpan elapsed
    )
    {
        var access = context.Check is { } check ? AccessTag(check.Access) : "none";
        services.GetRequiredService<ServerMetrics>().McpToolCall(tool, outcome, access, elapsed);
        LogToolCall(
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Custos.Server.Mcp"),
            tool,
            context.CallerId ?? 0,
            context.GuildId ?? 0,
            outcome,
            Math.Round(elapsed.TotalMilliseconds, 1)
        );
    }

    /// <summary>The kinds of access as a tag, for example <c>read,report</c>.</summary>
    internal static string AccessTag(GuildAccess access) =>
        access == GuildAccess.None ? "none" : string.Join(',', GuildTools.Kinds(access));

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Audit: MCP tool {Tool} by user {UserId} in guild {GuildId}: {Outcome} in "
            + "{ElapsedMs} ms"
    )]
    private static partial void LogToolCall(
        ILogger logger,
        string tool,
        ulong userId,
        ulong guildId,
        string outcome,
        double elapsedMs
    );
}
