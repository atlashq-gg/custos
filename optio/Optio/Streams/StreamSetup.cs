using NATS.Client.Core;
using Optio.Discord;
using Optio.Events;

namespace Optio.Streams;

internal static class StreamSetup
{
    /// <summary>
    /// Registers the stream when the configuration has the broker and the Discord token. Without
    /// them, Optio has no stream, and the discovery document has no <c>stream_endpoint</c>. The
    /// connection does not wait for the broker: the client connects at the first use, and again
    /// after each failure.
    /// </summary>
    public static IHostApplicationBuilder AddOptioStream(this IHostApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(OptioOptions.SectionName).Get<OptioOptions>()
            ?? new OptioOptions();
        if (
            builder.Configuration.GetConnectionString(OptioOptions.BrokerConnectionName)
                is not { Length: > 0 } url
            || string.IsNullOrEmpty(options.DiscordToken)
        )
        {
            return builder;
        }

        builder.Services.AddSingleton(services => new NatsConnection(
            new NatsOpts
            {
                Url = url,
                Name = "optio",
                InboxPrefix = InboxPrefix(url),
                RetryOnInitialConnect = true,
                LoggerFactory = services.GetRequiredService<ILoggerFactory>(),
            }
        ));
        builder.Services.AddSingleton<EventStream>();
        builder.Services.AddHostedService(services => services.GetRequiredService<EventStream>());
        builder.Services.AddSingleton<IMemberDirectory, DiscordMembers>();
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<IMemberDirectory>()
        );
        builder.Services.AddSingleton<OpenStreams>();
        return builder;
    }

    /// <summary>The reply subjects of the broker user in the URL, so that no other user reads the
    /// replies of the broker.</summary>
    private static string InboxPrefix(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.UserInfo.Split(':')[0] is { Length: > 0 } user
            ? $"_INBOX.{Uri.UnescapeDataString(user)}"
            : "_INBOX";
}
