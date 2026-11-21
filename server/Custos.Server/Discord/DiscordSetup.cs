using Custos.Server.Telemetry;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

namespace Custos.Server.Discord;

/// <summary>
/// The connection to Discord as a bot. Without <c>Discord:Token</c> the connection is off: the
/// server registers no gateway client and no command.
/// </summary>
internal static partial class DiscordSetup
{
    public const string TokenKey = "Discord:Token";

    public static bool IsEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[TokenKey]);

    public static IHostApplicationBuilder AddCustosDiscord(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<GuildSettingsStore>();
        if (!IsEnabled(builder.Configuration))
            return builder;

        builder
            .Services.AddDiscordGateway(
                (options, services) =>
                {
                    // Guilds keeps the guilds and their roles in the cache. The server needs no
                    // privileged intent: it reads a member with Get Guild Member.
                    options.Intents = GatewayIntents.Guilds;
                    options.RestClientConfiguration = new RestClientConfiguration
                    {
                        RequestHandler = new MeteredRestRequestHandler(
                            new RestRequestHandler(),
                            services.GetRequiredService<ServerMetrics>()
                        ),
                    };
                }
            )
            .AddApplicationCommands(commands =>
                commands.ResultHandler =
                    ApplicationCommandResultHandler<ApplicationCommandContext>.Ephemeral
            )
            .AddComponentInteractions<ModalInteraction, ModalInteractionContext>();

        builder.Services.AddScoped<IDiscordDirectory, GatewayDirectory>();
        builder.Services.AddScoped<GuildAccessEvaluator>();
        builder.Services.AddHttpClient<SettingsAttachmentReader>();
        builder.Services.AddTransient<StructureAutocomplete>();
        return builder;
    }

    /// <summary>Registers the commands, or writes a warning when the connection is off.</summary>
    public static WebApplication MapCustosDiscord(this WebApplication app)
    {
        if (IsEnabled(app.Configuration))
            app.AddModules(typeof(DiscordSetup).Assembly);
        else
            LogDiscordOff(app.Logger);
        return app;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Discord:Token is not set. The Discord connection is off: an upload needs no "
            + "role, and the commands are not available."
    )]
    private static partial void LogDiscordOff(ILogger logger);
}
