using Microsoft.Extensions.Options;
using Optio.Storage;

namespace Optio.Discord;

internal static class DiscordSetup
{
    /// <summary>
    /// Registers the gateway of the bot of Optio when the configuration has its token. With the
    /// database, the gateway also serves <c>/optioadmin</c>.
    /// </summary>
    public static IHostApplicationBuilder AddOptioDiscord(this IHostApplicationBuilder builder)
    {
        var token = builder.Configuration[$"{OptioOptions.SectionName}:DiscordToken"];
        if (string.IsNullOrWhiteSpace(token))
            return builder;

        var commands = StorageSetup.IsEnabled(builder.Configuration);
        if (commands)
            builder.Services.AddHttpClient<SettingsAttachmentReader>();
        builder.Services.AddSingleton(services => new DiscordGateway(
            services.GetRequiredService<IOptions<OptioOptions>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ILogger<DiscordGateway>>(),
            commands
        ));
        builder.Services.AddHostedService(services =>
        {
            // The member directory of the stream follows the gateway from READY on. The host
            // resolves each hosted service before it starts the first, so the directory exists
            // before the gateway connects.
            services.GetService<IMemberDirectory>();
            return services.GetRequiredService<DiscordGateway>();
        });
        return builder;
    }
}
