using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Optio.Events;
using Optio.Telemetry;
using Optio.Tests.Infrastructure;
using static Optio.Tests.Infrastructure.FakeMembers;

namespace Optio.Tests;

/// <summary>The stream of spec/05-stream.md, with a fake Discord and the events of a NATS
/// container (optio/docs/producers.md). Needs Docker.</summary>
[NonParallelizable]
public sealed class StreamTests
{
    private const string Path = "/api/v1/stream";

    private NatsBroker _broker = null!;
    private OptioFactory _factory = null!;

    [OneTimeSetUp]
    public async Task StartBrokerAsync() => _broker = await NatsBroker.StartAsync();

    [OneTimeTearDown]
    public async Task StopBrokerAsync() => await _broker.DisposeAsync();

    [SetUp]
    public async Task SetUpAsync()
    {
        _factory = new OptioFactory(stream: true, broker: _broker.Url("optio"));
        await _factory.WaitForStreamAsync();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Discovery_document_has_the_stream_endpoint_only_with_the_stream()
    {
        using var without = new OptioFactory();
        var on = await _factory
            .CreateClient()
            .GetFromJsonAsync<JsonElement>("/.well-known/custos-configuration");
        var off = await without
            .CreateClient()
            .GetFromJsonAsync<JsonElement>("/.well-known/custos-configuration");

        SpecSchemas.AssertDiscoveryDocument(on);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                on.GetProperty("stream_endpoint").GetString(),
                Is.EqualTo(OptioFactory.PublicBaseUrl + Path)
            );
            Assert.That(off.TryGetProperty("stream_endpoint", out _), Is.False);
        }
    }

    [Test]
    public async Task Stream_first_sends_its_position_with_no_data()
    {
        var info = await _broker.InfoAsync();

        var (stream, position) = await OpenAsync();

        using (stream)
        {
            Assert.That(
                position,
                Is.EqualTo(
                    new Position(
                        info.Created.ToUnixTimeMilliseconds(),
                        info.State.LastSeq
                    ).ToString()
                )
            );
        }
    }

    [Test]
    public async Task Member_gets_the_event_that_a_role_of_the_member_can_read()
    {
        var (stream, position) = await OpenAsync();
        using var _ = stream;

        await PublishAsync(
            "Depot, Main\nRifle crates +20 (40)",
            roles: [ReadRole],
            reference: "stockpile:1"
        );

        var item = await stream.NextAsync();
        SpecSchemas.AssertMessage(item!.Data!.Value);
        var start = Position.Parse(position)!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                item.Id,
                Is.EqualTo((start with { Sequence = start.Sequence + 1 }).ToString())
            );
            Assert.That(item.Data.Value.GetProperty("kind").GetString(), Is.EqualTo("event"));
            Assert.That(
                item.Data.Value.GetProperty("text").GetString(),
                Is.EqualTo("Depot, Main\nRifle crates +20 (40)")
            );
            Assert.That(
                item.Data.Value.GetProperty("reference").GetString(),
                Is.EqualTo("stockpile:1")
            );
            Assert.That(item.Data.Value.GetProperty("source").GetString(), Is.EqualTo("custos"));
        }
    }

    [Test]
    public async Task Member_gets_no_event_of_another_guild_or_of_a_role_that_the_member_lacks()
    {
        var (stream, _) = await OpenAsync();
        using var _1 = stream;

        await PublishAsync("other guild", guild: OtherGuild, roles: [OtherGuild]);
        await PublishAsync("other role", roles: [OtherRole]);
        await PublishAsync("empty list", roles: []);
        await PublishAsync("everyone", roles: [Guild]);

        Assert.That(await TextAsync(stream), Is.EqualTo("everyone"));
    }

    [Test]
    public async Task Owner_gets_an_event_with_an_empty_role_list()
    {
        var (stream, _) = await OpenAsync(Owner);
        using var _1 = stream;

        await PublishAsync("for the administrators", roles: []);

        Assert.That(await TextAsync(stream), Is.EqualTo("for the administrators"));
    }

    [Test]
    public async Task Event_that_does_not_agree_with_its_subject_is_not_sent_and_is_counted()
    {
        using var events = new MetricCollector<long>(
            _factory.Services.GetRequiredService<OptioMetrics>().Meter,
            OptioMetrics.Events
        );
        var (stream, _) = await OpenAsync();
        using var _1 = stream;

        // The guild of the subject is not the guild of the policy.
        await _broker.PublishAsync(
            Subject(Guild),
            Event("another policy guild", OtherGuild, [OtherGuild])
        );
        // The source of the subject is not the source of the event.
        await _broker.PublishAsync(Subject(Guild), Event("other source", source: "other"));
        await _broker.PublishAsync(Subject(Guild), Event("version 2", version: 2));
        await _broker.PublishAsync(Subject(Guild), "not json"u8.ToArray());
        await PublishAsync("valid", roles: [ReadRole]);

        Assert.That(await TextAsync(stream), Is.EqualTo("valid"));
        Assert.That(
            events.GetMeasurementSnapshot().Select(m => m.Tags[OptioMetrics.OutcomeTag]),
            Is.EqualTo(new[] { "invalid", "invalid", "invalid", "invalid", "sent" })
        );
    }

    [Test]
    public async Task Stream_with_a_last_event_id_gets_the_events_after_it()
    {
        string first;
        var (stream, _) = await OpenAsync();
        using (stream)
        {
            await PublishAsync("one", roles: [ReadRole]);
            first = (await stream.NextAsync())!.Id!;
        }
        await PublishAsync("two", roles: [ReadRole]);
        await PublishAsync("three", roles: [ReadRole]);

        var (resumed, position) = await OpenAsync(lastEventId: first);
        using var _ = resumed;

        Assert.That(position, Is.EqualTo(first));
        Assert.That(await TextAsync(resumed), Is.EqualTo("two"));
        Assert.That(await TextAsync(resumed), Is.EqualTo("three"));
    }

    [TestCase(null)]
    [TestCase("not an id")]
    [TestCase("1-1")] // Another stream.
    [TestCase("after the end")]
    public async Task Stream_without_a_valid_last_event_id_starts_from_now(string? lastEventId)
    {
        await PublishAsync("old", roles: [ReadRole]);
        var info = await _broker.InfoAsync();
        if (lastEventId == "after the end")
        {
            lastEventId = new Position(
                info.Created.ToUnixTimeMilliseconds(),
                info.State.LastSeq + 10
            ).ToString();
        }
        var (stream, position) = await OpenAsync(lastEventId: lastEventId);
        using var _ = stream;

        await PublishAsync("new", roles: [ReadRole]);

        Assert.That(Position.Parse(position)!.Value.Sequence, Is.EqualTo(info.State.LastSeq));
        Assert.That(await TextAsync(stream), Is.EqualTo("new"));
    }

    [Test]
    public async Task Event_waits_until_the_guild_is_ready()
    {
        using var events = new MetricCollector<long>(
            _factory.Services.GetRequiredService<OptioMetrics>().Meter,
            OptioMetrics.Events
        );
        var (stream, _) = await OpenAsync();
        using var _1 = stream;
        _factory.Members.NotReady.Add(Guild);
        await PublishAsync("held", roles: [ReadRole]);
        await Task.Delay(500);
        Assert.That(events.GetMeasurementSnapshot(), Is.Empty);

        _factory.Members.MakeReady(Guild);

        Assert.That(await TextAsync(stream), Is.EqualTo("held"));
    }

    [Test]
    public async Task Stream_sends_a_comment_when_it_has_no_event()
    {
        var (stream, _) = await OpenAsync();
        using var _1 = stream;

        // The stream can make its timer after the first advance. The second advance then fires it.
        _factory.Time.Advance(TimeSpan.FromSeconds(15));
        await Task.Delay(200);
        _factory.Time.Advance(TimeSpan.FromSeconds(15));

        Assert.That((await stream.NextAsync())!.Comment, Is.EqualTo("keep-alive"));
    }

    [Test]
    public async Task Comment_comes_15_seconds_after_the_last_write_also_with_denied_events()
    {
        using var events = new MetricCollector<long>(
            _factory.Services.GetRequiredService<OptioMetrics>().Meter,
            OptioMetrics.Events
        );
        var (stream, _) = await OpenAsync();
        using var _1 = stream;
        await Task.Delay(200);
        _factory.Time.Advance(TimeSpan.FromSeconds(10));

        await PublishAsync("denied", roles: [OtherRole]);
        await events.WaitForMeasurementsAsync(1, TimeSpan.FromSeconds(10));
        _factory.Time.Advance(TimeSpan.FromSeconds(5));

        Assert.That((await stream.NextAsync())!.Comment, Is.EqualTo("keep-alive"));
    }

    [Test]
    public async Task Stream_ends_when_the_token_expires()
    {
        var (stream, _) = await OpenAsync();
        using var _1 = stream;

        _factory.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.That(await stream.NextAsync(), Is.Null);
        Assert.That(() => _factory.Members.Connected, Is.Zero.After(5000, 50));
    }

    [Test]
    public async Task Stream_ends_when_the_broker_stream_goes_and_a_new_one_has_a_new_identity()
    {
        var (stream, position) = await OpenAsync();
        using (stream)
        {
            await _broker.DeleteStreamAsync();

            Assert.That(await stream.NextAsync(), Is.Null);
        }

        // Optio makes the stream again. The old position starts from now.
        var (again, newPosition) = await OpenAsync(lastEventId: position);
        using var _ = again;
        await PublishAsync("after", roles: [ReadRole]);

        Assert.That(
            Position.Parse(newPosition)!.Value.Stream,
            Is.Not.EqualTo(Position.Parse(position)!.Value.Stream)
        );
        Assert.That(await TextAsync(again), Is.EqualTo("after"));
    }

    [Test]
    public async Task Stream_of_a_guild_without_the_bot_gets_one_error()
    {
        _factory.Members.BotGuilds.Clear();
        var (stream, _) = await OpenAsync();
        using var _1 = stream;

        var item = await stream.NextAsync();

        SpecSchemas.AssertMessage(item!.Data!.Value);
        Assert.That(item.Data.Value.GetProperty("kind").GetString(), Is.EqualTo("error"));
    }

    [Test]
    public async Task Stream_gets_503_without_the_broker()
    {
        using var factory = new OptioFactory(stream: true);
        using var client = factory.CreateMemberClient();

        using var response = await client.GetAsync(Path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task Token_without_a_guild_gets_403()
    {
        using var client = _factory.CreateCustosClient(
            claims: [new Claim("discord_id", Reader.ToString())]
        );

        using var response = await client.GetAsync(Path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task User_that_is_not_a_member_gets_403()
    {
        using var client = _factory.CreateMemberClient(400000000000000099);

        using var response = await client.GetAsync(Path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(_factory.Members.Connected, Is.Zero);
    }

    [Test]
    public async Task Stream_gets_503_when_Discord_does_not_answer()
    {
        _factory.Members.Unavailable = true;
        using var client = _factory.CreateMemberClient();

        using var response = await client.GetAsync(Path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task User_with_too_many_open_streams_gets_429()
    {
        List<SseReader> open = [];
        for (var i = 0; i < 5; i++)
            open.Add((await OpenAsync()).Stream);
        using var client = _factory.CreateMemberClient();

        using var response = await client.GetAsync(Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(60)));
        }
        open.ForEach(s => s.Dispose());
    }

    /// <summary>Opens a stream and reads its first event: the position.</summary>
    private async Task<(SseReader Stream, string Position)> OpenAsync(
        ulong user = Reader,
        string? lastEventId = null
    )
    {
        var client = _factory.CreateMemberClient(user);
        var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (lastEventId is not null)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo("text/event-stream")
        );
        var stream = new SseReader(response);
        var first = await stream.NextAsync();
        Assert.That(first?.Id, Is.Not.Null);
        Assert.That(first!.Data, Is.Null);
        return (stream, first.Id!);
    }

    private Task PublishAsync(
        string text,
        IReadOnlyList<ulong> roles,
        ulong guild = Guild,
        string? reference = null
    ) => _broker.PublishAsync(Subject(guild), Event(text, guild, roles, reference: reference));

    private static string Subject(ulong guild) => $"events.custos.{guild}.stockpile.changed";

    /// <summary>A CloudEvents document of optio/docs/producers.md.</summary>
    private static byte[] Event(
        string text,
        ulong guild = Guild,
        IReadOnlyList<ulong>? roles = null,
        string source = "custos",
        int version = 1,
        string? reference = null
    )
    {
        var message = new JsonObject { ["text"] = text };
        if (reference is not null)
            message["reference"] = reference;
        return JsonSerializer.SerializeToUtf8Bytes(
            new JsonObject
            {
                ["specversion"] = "1.0",
                ["id"] = Guid.NewGuid().ToString(),
                ["source"] = source,
                ["type"] = "stockpile.changed",
                ["time"] = DateTimeOffset.UtcNow,
                ["data"] = new JsonObject
                {
                    ["policy"] = new JsonObject
                    {
                        ["version"] = version,
                        ["guild"] = guild.ToString(),
                        ["roles"] = new JsonArray([
                            .. (roles ?? [ReadRole]).Select(r => (JsonNode)r.ToString()),
                        ]),
                    },
                    ["message"] = message,
                },
            }
        );
    }

    private static async Task<string?> TextAsync(SseReader stream) =>
        (await stream.NextAsync())?.Data?.GetProperty("text").GetString();
}
