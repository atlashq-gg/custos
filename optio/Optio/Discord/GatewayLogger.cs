using NetCord.Logging;
using LogLevel = NetCord.Logging.LogLevel;

namespace Optio.Discord;

/// <summary>
/// Writes the log of the NetCord gateway to the log of Optio: the connection, a close with its
/// code, and a reconnection. Without it, NetCord writes nothing.
/// </summary>
internal sealed class GatewayLogger(ILogger logger) : IGatewayLogger
{
    // The two enums have the same values in the same order, from Trace to None.
    public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(Map(logLevel));

    public void Log<TState>(
        LogLevel logLevel,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => logger.Log(Map(logLevel), default, state, exception, formatter);

    private static Microsoft.Extensions.Logging.LogLevel Map(LogLevel logLevel) =>
        (Microsoft.Extensions.Logging.LogLevel)logLevel;
}
