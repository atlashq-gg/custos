using Microsoft.Extensions.Logging.Testing;
using Optio.Discord;
using GatewayLevel = NetCord.Logging.LogLevel;

namespace Optio.Tests;

/// <summary>The log of the NetCord gateway in the log of Optio.</summary>
public sealed class GatewayLoggerTests
{
    [Test]
    public void Each_level_of_NetCord_has_the_level_of_the_same_name()
    {
        var log = new FakeLogCollector();
        var logger = new GatewayLogger(new FakeLogger(log));

        foreach (var level in Enum.GetValues<GatewayLevel>().Where(l => l != GatewayLevel.None))
            logger.Log(level, level.ToString(), null, (state, _) => state);

        Assert.That(
            log.GetSnapshot().Select(r => (r.Level.ToString(), r.Message)),
            Is.EqualTo(Enum.GetNames<GatewayLevel>().Where(n => n != "None").Select(n => (n, n)))
        );
    }
}
