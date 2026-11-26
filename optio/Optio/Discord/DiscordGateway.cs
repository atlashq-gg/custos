using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

namespace Optio.Discord;

/// <summary>
/// The Discord gateway of the bot of Optio, with the intent <c>GUILD_MEMBERS</c>. The member
/// directory reads its events. With the settings store, it also serves the commands of Optio
/// (<see cref="AdminCommands"/>) and registers them at the first <c>READY</c>.
/// </summary>
internal sealed partial class DiscordGateway : IHostedService, IDisposable
{
    private readonly ApplicationCommandService<ApplicationCommandContext> _commands = new();
    private readonly ComponentInteractionService<ModalInteractionContext> _modals = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DiscordGateway> _logger;
    private int _registered;

    public DiscordGateway(
        IOptions<OptioOptions> options,
        IServiceScopeFactory scopes,
        ILogger<DiscordGateway> logger,
        bool commands
    )
    {
        _scopes = scopes;
        _logger = logger;
        Client = new GatewayClient(
            new BotToken(options.Value.DiscordToken!),
            new GatewayClientConfiguration
            {
                Intents = GatewayIntents.Guilds | GatewayIntents.GuildUsers,
                Logger = new GatewayLogger(logger),
            }
        );
        Client.RateLimited += OnRateLimited;
        if (!commands)
            return;

        _commands.AddModules(typeof(DiscordGateway).Assembly);
        _modals.AddModules(typeof(DiscordGateway).Assembly);
        Client.Ready += RegisterAsync;
        Client.InteractionCreate += HandleAsync;
    }

    public GatewayClient Client { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Client.StartAsync(cancellationToken: cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Optio still serves the upload. A stream gets 503 until Discord answers.
            LogStartFailed(_logger, e);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Client.CloseAsync(cancellationToken: cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The connection is already closed.
        }
    }

    public void Dispose() => Client.Dispose();

    private async ValueTask RegisterAsync(ReadyEventArgs args)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;
        try
        {
            var manager = new ApplicationCommandServiceManager();
            manager.AddService(_commands);
            await manager.RegisterCommandsAsync(Client.Rest, args.ApplicationId);
        }
        catch (Exception e)
        {
            Interlocked.Exchange(ref _registered, 0);
            LogRegisterFailed(_logger, e);
        }
    }

    /// <summary>Discord answers a rate-limited request with this event only. For a member request,
    /// a stream then waits until its timeout.</summary>
    private ValueTask OnRateLimited(RateLimitedEventArgs args)
    {
        LogRateLimited(
            _logger,
            args.Opcode,
            (args.Metadata as RequestGuildUsersRateLimitMetadata)?.GuildId,
            args.RetryAfter
        );
        return default;
    }

    private async ValueTask HandleAsync(Interaction interaction)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var result = interaction switch
        {
            ApplicationCommandInteraction command => await _commands.ExecuteAsync(
                new ApplicationCommandContext(command, Client),
                scope.ServiceProvider
            ),
            ModalInteraction modal => await _modals.ExecuteAsync(
                new ModalInteractionContext(modal, Client),
                scope.ServiceProvider
            ),
            _ => null,
        };
        if (result is not IFailResult fail)
            return;

        if (fail is IExceptionResult exception)
            LogCommandFailed(_logger, exception.Exception);
        try
        {
            await interaction.SendResponseAsync(
                InteractionCallback.Message(
                    new InteractionMessageProperties
                    {
                        Content = fail.Message,
                        Flags = MessageFlags.Ephemeral,
                    }
                )
            );
        }
        catch (RestException)
        {
            // The command already answered.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The Discord gateway did not start")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The commands of Optio were not registered")]
    private static partial void LogRegisterFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Discord rate limited the gateway request {Opcode} of guild {GuildId}. "
            + "Retry after {RetryAfter} s"
    )]
    private static partial void LogRateLimited(
        ILogger logger,
        GatewayOpcode opcode,
        ulong? guildId,
        double retryAfter
    );

    [LoggerMessage(Level = LogLevel.Error, Message = "A command of Optio failed")]
    private static partial void LogCommandFailed(ILogger logger, Exception exception);
}
