using Optio.Discord;
using Optio.Events;
using RabbitMQ.Client;

namespace Optio.Streams;

internal static class StreamSetup
{
    /// <summary>
    /// Registers the stream when the configuration has the broker, one source or more, and the
    /// Discord token. Without them, Optio has no stream, and the discovery document has no
    /// <c>stream_endpoint</c>.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        var options = configuration.GetSection(OptioOptions.SectionName).Get<OptioOptions>()
            ?? new OptioOptions();
        return !string.IsNullOrWhiteSpace(
                configuration.GetConnectionString(OptioOptions.BrokerConnectionName)
            )
            && options.Sources.Length > 0
            && !string.IsNullOrEmpty(options.DiscordToken);
    }

    public static IHostApplicationBuilder AddOptioStream(this IHostApplicationBuilder builder)
    {
        if (!IsEnabled(builder.Configuration))
            return builder;

        // The consumer connects again itself, from its last offset.
        builder.Services.AddSingleton(
            new ConnectionFactory
            {
                Uri = new Uri(
                    builder.Configuration.GetConnectionString(OptioOptions.BrokerConnectionName)!
                ),
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false,
                ClientProvidedName = "optio",
            }
        );
        builder.Services.AddSingleton<IMemberDirectory, DiscordMembers>();
        builder.Services.AddSingleton<StreamHub>();
        builder.Services.AddHostedService<EventConsumer>();
        return builder;
    }
}
