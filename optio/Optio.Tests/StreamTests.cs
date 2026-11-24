using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Optio.Events;
using Optio.Streams;
using Optio.Tests.Infrastructure;
using static Optio.Tests.Infrastructure.FakeMembers;

namespace Optio.Tests;

/// <summary>The stream of spec/05-stream.md, with a fake Discord and the events given to the hub.
/// </summary>
public sealed class StreamTests
{
    private const string Path = "/api/v1/stream";

    private OptioFactory _factory = null!;
    private long _offset;

    [SetUp]
    public void SetUp()
    {
        _factory = new OptioFactory(stream: true);
        _offset = 0;
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
    public async Task Member_gets_the_event_that_a_role_of_the_member_can_read()
    {
        using var stream = await OpenAsync();

        Publish("Depot, Main\nRifle crates +20 (40)", roles: [ReadRole], reference: "stockpile:1");

        var item = await stream.NextAsync();
        SpecSchemas.AssertMessage(item!.Data!.Value);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                item.Id,
                Is.EqualTo(Cursor.Encode(new Dictionary<string, long> { ["custos"] = 1 }))
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
        using var stream = await OpenAsync();

        Publish("other guild", guild: OtherGuild, roles: [OtherGuild]);
        Publish("other role", roles: [OtherRole]);
        Publish("empty list", roles: []);
        Publish("everyone", roles: [Guild]);

        Assert.That(await TextAsync(stream), Is.EqualTo("everyone"));
    }

    [Test]
    public async Task Owner_gets_an_event_with_an_empty_role_list()
    {
        using var stream = await OpenAsync(Owner);

        Publish("for the administrators", roles: []);

        Assert.That(await TextAsync(stream), Is.EqualTo("for the administrators"));
    }

    [Test]
    public async Task Stream_with_a_last_event_id_gets_the_events_after_it()
    {
        string first;
        using (var stream = await OpenAsync())
        {
            Publish("one", roles: [ReadRole]);
            first = (await stream.NextAsync())!.Id!;
        }
        Publish("two", roles: [ReadRole]);
        Publish("three", roles: [ReadRole]);

        using var resumed = await OpenAsync(lastEventId: first);

        Assert.That(await TextAsync(resumed), Is.EqualTo("two"));
        Assert.That(await TextAsync(resumed), Is.EqualTo("three"));
    }

    [TestCase(null)]
    [TestCase("not an id")]
    [TestCase("eyJjdXN0b3MiOjk5fQ")] // {"custos":99}: after the last offset.
    public async Task Stream_without_a_valid_last_event_id_starts_from_now(string? lastEventId)
    {
        Publish("old", roles: [ReadRole]);
        using var stream = await OpenAsync(lastEventId: lastEventId);

        Publish("new", roles: [ReadRole]);

        Assert.That(await TextAsync(stream), Is.EqualTo("new"));
    }

    [Test]
    public async Task Replay_leaves_out_the_events_older_than_the_window()
    {
        var start = Cursor.Encode(new Dictionary<string, long> { ["custos"] = 0 });
        Publish("old", roles: [ReadRole], age: TimeSpan.FromMinutes(16));
        Publish("new", roles: [ReadRole]);

        using var stream = await OpenAsync(lastEventId: start);

        Assert.That(await TextAsync(stream), Is.EqualTo("new"));
    }

    [Test]
    public async Task Event_waits_until_the_guild_is_ready()
    {
        using var stream = await OpenAsync();
        _factory.Members.NotReady.Add(Guild);
        Publish("held", roles: [ReadRole]);

        _factory.Members.MakeReady(Guild);

        Assert.That(await TextAsync(stream), Is.EqualTo("held"));
    }

    [Test]
    public async Task Stream_sends_a_comment_when_it_has_no_event()
    {
        using var stream = await OpenAsync();

        _factory.Time.Advance(TimeSpan.FromSeconds(15));

        Assert.That((await stream.NextAsync())!.Comment, Is.EqualTo("keep-alive"));
    }

    [Test]
    public async Task Stream_ends_when_the_token_expires()
    {
        using var stream = await OpenAsync();

        _factory.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.That(await stream.NextAsync(), Is.Null);
        Assert.That(() => _factory.Members.Connected, Is.Zero.After(5000, 50));
    }

    [Test]
    public async Task Stream_of_a_guild_without_the_bot_gets_one_error()
    {
        _factory.Members.BotGuilds.Clear();
        using var stream = await OpenAsync();

        var item = await stream.NextAsync();

        SpecSchemas.AssertMessage(item!.Data!.Value);
        Assert.That(item.Data.Value.GetProperty("kind").GetString(), Is.EqualTo("error"));
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
            open.Add(await OpenAsync());
        using var client = _factory.CreateMemberClient();

        using var response = await client.GetAsync(Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(60)));
        }
        open.ForEach(s => s.Dispose());
    }

    private async Task<SseReader> OpenAsync(ulong user = Reader, string? lastEventId = null)
    {
        var client = _factory.CreateMemberClient(user);
        var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (lastEventId is not null)
            request.Headers.Add("Last-Event-ID", lastEventId);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo("text/event-stream")
        );
        return new SseReader(response);
    }

    private void Publish(
        string text,
        IReadOnlyList<ulong> roles,
        ulong guild = Guild,
        string? reference = null,
        TimeSpan age = default
    ) =>
        _factory.Hub.Publish(
            new ProducerEvent(
                "custos",
                ++_offset,
                _factory.Time.GetUtcNow() - age,
                new EventPolicy(guild, roles, null),
                text,
                reference
            )
        );

    private static async Task<string?> TextAsync(SseReader stream) =>
        (await stream.NextAsync())?.Data?.GetProperty("text").GetString();
}
