using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Custos.Server.Discord;
using Custos.Server.Events;
using Custos.Server.Telemetry;
using Custos.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using static Custos.Server.Tests.Infrastructure.UploadRequests;

namespace Custos.Server.Tests.Events;

/// <summary>The events of the server on the broker (optio/docs/producers.md). Needs
/// Docker.</summary>
public sealed class EventTests
{
    private const ulong Reader = 300000000000000001;
    private const ulong Sharer = 300000000000000002;
    private const ulong ReaderRole = 300000000000000011;
    private const ulong SharerRole = 300000000000000012;
    private const string Stockpile = "ATLAS REC";
    private static readonly DateTimeOffset Seen = FakeWarApi.War142.StartedAt!.Value.AddDays(1);

    private Broker _broker = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _broker = await Broker.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await _broker.DisposeAsync();

    [Test]
    public async Task Change_gives_an_event_with_the_policy_of_the_origin_and_of_each_reader_guild()
    {
        using var factory = new CustosFactory(events: _broker.ConnectionString);
        await ShareAsync(factory);
        await using var reader = await _broker.ReadAsync();

        await UploadAsync(factory, Sharer, 5, Seen);

        var events = new[] { await reader.NextAsync(), await reader.NextAsync() }
            .OrderBy(e => e.RoutingKey)
            .ToList();
        using var db = factory.CreateDb();
        var stockpile = await db.Stockpiles.SingleAsync(s => s.GuildId == (long)Sharer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                events.Select(e => e.RoutingKey),
                Is.EqualTo(new[] { $"{Reader}.stockpile.changed", $"{Sharer}.stockpile.changed" })
            );
            foreach (
                var (received, guild, role) in events.Zip(
                    [(Reader, ReaderRole), (Sharer, SharerRole)],
                    (e, g) => (e, g.Item1, g.Item2)
                )
            )
            {
                SpecSchemas.AssertEvent(received.Body);
                Assert.That(received.ContentType, Is.EqualTo(EventPublisher.ContentType));
                Assert.That(received.Body.GetProperty("source").GetString(), Is.EqualTo("custos"));
                var data = received.Body.GetProperty("data");
                var policy = data.GetProperty("policy");
                Assert.That(policy.GetProperty("guild").GetString(), Is.EqualTo($"{guild}"));
                Assert.That(Strings(policy.GetProperty("roles")), Is.EqualTo(new[] { $"{role}" }));
                Assert.That(data.GetProperty("guild_id").GetString(), Is.EqualTo($"{guild}"));
                Assert.That(data.GetProperty("origin_guild").GetString(), Is.EqualTo($"{Sharer}"));
                Assert.That(
                    data.GetProperty("stockpile_id").GetString(),
                    Is.EqualTo(stockpile.StockpileId.ToString())
                );
                Assert.That(data.GetProperty("stockpile_name").GetString(), Is.EqualTo(Stockpile));
                Assert.That(data.GetProperty("war").GetInt32(), Is.EqualTo(142));
                Assert.That(
                    data.GetProperty("message").GetProperty("text").GetString(),
                    Does.EndWith($", {Stockpile}: new, 1 item")
                );
                Assert.That(
                    data.GetProperty("message").GetProperty("reference").GetString(),
                    Is.EqualTo($"stockpile:{stockpile.StockpileId}")
                );
            }
        }
    }

    [Test]
    public async Task Text_gives_the_change_with_the_label_that_each_guild_sees()
    {
        using var factory = new CustosFactory(events: _broker.ConnectionString);
        await ShareAsync(factory);
        await using var reader = await _broker.ReadAsync();
        // The reader guild has its own record of the structure, with its own label.
        await UploadAsync(factory, Reader, 1, Seen);
        await reader.NextAsync();
        using (var db = factory.CreateDb())
        {
            await db
                .Structures.Where(s => s.GuildId == (long)Reader)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Label, "Reader Depot"));
        }
        await UploadAsync(factory, Sharer, 5, Seen);
        await reader.NextAsync();
        await reader.NextAsync();

        await UploadAsync(factory, Sharer, 7, Seen.AddHours(1));

        var texts = new[] { await reader.NextAsync(), await reader.NextAsync() }.ToDictionary(
            e => e.RoutingKey,
            e => e.Body.GetProperty("data").GetProperty("message").GetProperty("text").GetString()
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                texts[$"{Reader}.stockpile.changed"],
                Is.EqualTo($"Reader Depot, {Stockpile}\nTest Rifle +2")
            );
            Assert.That(texts[$"{Sharer}.stockpile.changed"], Does.EndWith("\nTest Rifle +2"));
            Assert.That(texts[$"{Sharer}.stockpile.changed"], Does.Not.StartWith("Reader Depot"));
        }
    }

    [Test]
    public async Task Upload_without_a_stockpile_change_gives_no_event()
    {
        using var factory = new CustosFactory(events: _broker.ConnectionString);
        await using var reader = await _broker.ReadAsync();
        await UploadAsync(factory, Reader, 5, Seen);
        await reader.NextAsync();

        await UploadAsync(factory, Reader, 5, Seen.AddHours(1));

        Assert.That(await reader.AnyWithinAsync(TimeSpan.FromSeconds(2)), Is.False);
    }

    [Test]
    public async Task Upload_succeeds_when_the_broker_does_not_answer()
    {
        // Nothing listens on port 1.
        using var factory = new CustosFactory(events: "amqp://custos:custos@127.0.0.1:1");
        var watch = Stopwatch.StartNew();

        await UploadAsync(factory, Reader, 5, Seen);

        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public async Task Event_that_the_broker_returns_is_counted()
    {
        await using var unbound = await Broker.StartAsync(bound: false);
        using var factory = new CustosFactory(events: unbound.ConnectionString);
        using var events = new MetricCollector<long>(
            factory.Services.GetRequiredService<Meter>(),
            ServerMetrics.Events
        );

        await UploadAsync(factory, Reader, 5, Seen);

        await events.WaitForMeasurementsAsync(1, TimeSpan.FromSeconds(30));
        Assert.That(
            events.GetMeasurementSnapshot().Select(m => m.Tags[ServerMetrics.OutcomeTag]),
            Is.EqualTo(new[] { "returned" })
        );
    }

    private static string?[] Strings(JsonElement array) =>
        [.. array.EnumerateArray().Select(e => e.GetString())];

    private static async Task ShareAsync(CustosFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();
        await settings.SaveAsync(
            Sharer,
            GuildSettings.Default with
            {
                Access = new AccessSettings { ReadRoleIds = [SharerRole] },
                Sharing = new SharingSettings { ReadGuildIds = [Reader] },
            },
            1,
            "test",
            default
        );
        await settings.SaveAsync(
            Reader,
            GuildSettings.Default with
            {
                Access = new AccessSettings { ReadRoleIds = [ReaderRole] },
            },
            1,
            "test",
            default
        );
    }

    private static async Task UploadAsync(
        CustosFactory factory,
        ulong guild,
        short rifles,
        DateTimeOffset seen
    )
    {
        using var client = factory.CreateAuthorizedClient(guild: guild.ToString());
        var file = MapDataWriter.File(
            new Pinned(
                "OriginHex",
                0.25f,
                0.75f,
                "StorageFacility",
                seen,
                [],
                [(Stockpile, [("Items", "RifleC", rifles)])]
            )
        );
        using var response = await client.PostAsync(UploadPath, Body(file));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
