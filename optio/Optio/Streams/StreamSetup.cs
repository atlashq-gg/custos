using Optio.Discord;
using Optio.Events;
using RabbitMQ.Client;

namespace Optio.Streams;

internal static class StreamSetup
{
    /// <summary>
    /// Registers the stream when the configuration has the broker, one source or more, and the
    /// Discord token. Without them, optio has no stream, and the discovery document has no
    /// <c>stream_endpoint</c>.
    /// </summary>
    public static IHostApplicationBuilder AddOptioStream(this IHostApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(OptioOptions.SectionName).Get<OptioOptions>()
            ?? new OptioOptions();
        if (
            builder.Configuration.GetConnectionString(OptioOptions.BrokerConnectionName)
                is not { Length: > 0 } connection
            || options.Sources.Length == 0
            || string.IsNullOrEmpty(options.DiscordToken)
        )
        {
            return builder;
        }

        // The consumer connects again itself, from its last offset.
        builder.Services.AddSingleton(
            new ConnectionFactory
            {
                Uri = new Uri(connection),
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false,
                ClientProvidedName = "optio",
            }
        );
        builder.Services.AddSingleton<IMemberDirectory, DiscordMembers>();
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<IMemberDirectory>()
        );
        builder.Services.AddSingleton<StreamHub>();
        builder.Services.AddHostedService<EventConsumer>();
        return builder;
    }
}
