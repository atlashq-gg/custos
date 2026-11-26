using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Optio.Discord;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>The registration of the Discord gateway and of the member directory.</summary>
public sealed class DiscordSetupTests
{
    // Not a token, but its first part is a bot ID in Base64, as NetCord needs. The test does not
    // start the gateway.
    private static readonly string Token =
        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("1")) + ".placeholder";

    [Test]
    public async Task Host_makes_the_member_directory_before_it_starts_the_gateway()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [$"{OptioOptions.SectionName}:DiscordToken"] = Token }
        );
        builder.Services.Configure<OptioOptions>(
            builder.Configuration.GetSection(OptioOptions.SectionName)
        );
        // Without the stream services: the disposal of a NATS connection and an event stream that
        // never started damaged a shared buffer in some runs, and a later test then failed in
        // the module initializer of JsonSchema.Net.
        builder.AddOptioDiscord();
        var made = false;
        builder.Services.AddSingleton<IMemberDirectory>(_ =>
        {
            made = true;
            return new FakeMembers();
        });
        await using var services = builder.Services.BuildServiceProvider();

        _ = services.GetServices<IHostedService>().ToList();

        Assert.That(made, Is.True);
    }
}
