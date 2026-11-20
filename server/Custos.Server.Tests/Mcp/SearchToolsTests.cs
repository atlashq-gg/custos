using System.Net;
using System.Text.Json;
using Custos.Server.Discord;
using Custos.Server.Storage;
using Custos.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Custos.Server.Tests.Infrastructure.UploadRequests;
using static Custos.Server.Tests.Mcp.McpClients;

namespace Custos.Server.Tests.Mcp;

/// <summary>find_items and find_structures over the records of a guild and of the guilds that
/// share with it.</summary>
public sealed class SearchToolsTests
{
    /// <summary>The guild of the calls.</summary>
    private const ulong Reader = 300000000000000001;

    /// <summary>A guild that shares its records with <see cref="Reader"/>.</summary>
    private const ulong Sharer = 300000000000000002;

    /// <summary>A guild that shares with nobody.</summary>
    private const ulong Stranger = 300000000000000003;

    private const ulong User = 400000000000000001;
    private static readonly DateTimeOffset Seen = FakeWarApi.War142.StartedAt!.Value.AddDays(1);

    private CustosFactory _factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        var discord = new FakeDiscordDirectory()
            .Add(Reader, User, administrator: true)
            .Add(Sharer, User)
            .Add(Stranger, User);
        _factory = new CustosFactory(discord: discord, mcp: true);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<GuildSettingsStore>()
            .SaveAsync(
                Sharer,
                GuildSettings.Default with
                {
                    Sharing = new SharingSettings { ReadGuildIds = [Reader] },
                },
                User,
                "test",
                default
            );
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Find_items_gives_a_row_for_each_stockpile_and_a_total()
    {
        await UploadAsync(
            Reader,
            Depot(0.25f, 0.75f, Seen, [("ItemCrates", "RifleC", 20)], ("ATLAS REC", "RifleC", 5))
        );

        var result = await CallAsync("find_items", new() { ["items"] = new[] { "rifle" } });

        var rows = result.GetProperty("items").EnumerateArray().ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("total").GetInt32(), Is.EqualTo(2));
            Assert.That(
                rows.Select(r => r.GetProperty("quantity").GetInt32()),
                Is.EqualTo(new[] { 5, 20 })
            );
            Assert.That(
                rows[1].GetProperty("item").GetProperty("display_name").GetString(),
                Is.EqualTo("Test Rifle")
            );
            Assert.That(rows[1].GetProperty("crated").GetBoolean(), Is.True);
            Assert.That(rows[1].GetProperty("stockpile").GetString(), Is.EqualTo("Public"));
            Assert.That(rows[0].TryGetProperty("origin_guild", out _), Is.False);
            Assert.That(
                result
                    .GetProperty("applied")
                    .GetProperty("items")
                    .GetProperty("rifle")[0]
                    .GetString(),
                Is.EqualTo("RifleC")
            );
            Assert.That(
                result.GetProperty("applied").GetProperty("war").GetInt32(),
                Is.EqualTo(142)
            );
            Assert.That(
                result
                    .GetProperty("totals")
                    .EnumerateArray()
                    .Select(t => t.GetProperty("quantity").GetInt32()),
                Is.EqualTo(new[] { 5, 20 })
            );
        }
    }

    [Test]
    public async Task Stockpile_of_two_guilds_gives_one_row_from_the_latest_sighting()
    {
        await UploadAsync(Reader, Depot(0.25f, 0.75f, Seen, [], ("ATLAS REC", "RifleC", 5)));
        await UploadAsync(
            Sharer,
            Depot(0.25f, 0.75f, Seen.AddHours(1), [], ("ATLAS REC", "RifleC", 9))
        );
        await UploadAsync(Stranger, Depot(0.5f, 0.5f, Seen, [], ("SECRET", "RifleC", 100)));

        var result = await CallAsync("find_items", new() { ["items"] = new[] { "RifleC" } });

        var row = result.GetProperty("items").EnumerateArray().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.GetProperty("quantity").GetInt32(), Is.EqualTo(9));
            Assert.That(row.GetProperty("origin_guild").GetString(), Is.EqualTo(Sharer.ToString()));
        }
    }

    [Test]
    public async Task Label_of_the_reader_shows_when_it_has_its_own_record()
    {
        await UploadAsync(Reader, Depot(0.25f, 0.75f, Seen, [], ("ATLAS REC", "RifleC", 5)));
        await UploadAsync(Sharer, Depot(0.25f, 0.75f, Seen, [], ("ATLAS REC", "RifleC", 5)));
        await UploadAsync(Sharer, Depot(0.6f, 0.4f, Seen, [], ("NORTH", "RifleC", 5)));
        await SetLabelAsync(Reader, 0.25f, "Reader Depot");
        await SetLabelAsync(Sharer, 0.25f, "Sharer Depot");
        await SetLabelAsync(Sharer, 0.6f, "Sharer North");

        var result = await CallAsync("find_structures", []);

        Assert.That(
            result
                .GetProperty("items")
                .EnumerateArray()
                .Select(r => r.GetProperty("label").GetString()),
            Is.EqualTo(new[] { "Reader Depot", "Sharer North" })
        );
    }

    [Test]
    public async Task Find_structures_gives_the_stockpiles_and_their_entries()
    {
        await UploadAsync(
            Reader,
            Depot(0.25f, 0.75f, Seen, [("ItemCrates", "RifleC", 20)], ("ATLAS REC", "TruckC", 2))
        );

        var result = await CallAsync(
            "find_structures",
            new() { ["stockpiles"] = new[] { "public" } }
        );

        var structure = result.GetProperty("items").EnumerateArray().Single();
        var stockpile = structure.GetProperty("stockpiles").EnumerateArray().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(structure.GetProperty("type").GetString(), Is.EqualTo("Test Depot"));
            Assert.That(structure.GetProperty("category").GetString(), Is.EqualTo("Test Storage"));
            Assert.That(structure.GetProperty("region").GetString(), Is.EqualTo("Origin"));
            Assert.That(stockpile.GetProperty("name").GetString(), Is.EqualTo("Public"));
            Assert.That(
                stockpile
                    .GetProperty("entries")[0]
                    .GetProperty("item")
                    .GetProperty("code_name")
                    .GetString(),
                Is.EqualTo("RifleC")
            );
        }
    }

    [Test]
    public async Task Filters_of_two_lists_must_both_match()
    {
        await UploadAsync(
            Reader,
            Depot(0.25f, 0.75f, Seen, [("ItemCrates", "RifleC", 20)], ("ATLAS REC", "TruckC", 2))
        );

        var both = await CallAsync(
            "find_items",
            new() { ["items"] = new[] { "truck" }, ["stockpiles"] = new[] { "atlas" } }
        );
        var none = await CallAsync(
            "find_items",
            new() { ["items"] = new[] { "truck" }, ["stockpiles"] = new[] { "public" } }
        );
        var nothing = await CallAsync("find_items", new() { ["items"] = new[] { "no such item" } });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(both.GetProperty("total").GetInt32(), Is.EqualTo(1));
            Assert.That(none.GetProperty("total").GetInt32(), Is.Zero);
            Assert.That(nothing.GetProperty("total").GetInt32(), Is.Zero);
            Assert.That(
                nothing
                    .GetProperty("applied")
                    .GetProperty("items")
                    .GetProperty("no such item")
                    .GetArrayLength(),
                Is.Zero
            );
        }
    }

    [Test]
    public async Task Limit_cuts_the_list_and_the_cursor_gets_the_next_page()
    {
        await UploadAsync(
            Reader,
            Depot(0.25f, 0.75f, Seen, [("ItemCrates", "RifleC", 20), ("Items", "Cloth", 9)])
        );

        var first = await CallAsync("find_items", new() { ["limit"] = 1 });
        var second = await CallAsync(
            "find_items",
            new() { ["limit"] = 1, ["cursor"] = first.GetProperty("next_cursor").GetString() }
        );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.GetProperty("total").GetInt32(), Is.EqualTo(2));
            Assert.That(first.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
            Assert.That(second.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
            Assert.That(second.TryGetProperty("next_cursor", out _), Is.False);
            Assert.That(
                second
                    .GetProperty("items")[0]
                    .GetProperty("item")
                    .GetProperty("code_name")
                    .GetString(),
                Is.Not.EqualTo(
                    first
                        .GetProperty("items")[0]
                        .GetProperty("item")
                        .GetProperty("code_name")
                        .GetString()
                )
            );
        }
    }

    [Test]
    public async Task Unknown_war_is_an_error_result()
    {
        using var http = _factory.CreateMcpClient(User.ToString());
        await using var client = await ConnectAsync(http);

        var result = await client.CallToolAsync(
            "find_items",
            new Dictionary<string, object?> { ["guild_id"] = Reader.ToString(), ["war"] = 7 }
        );

        Assert.That(result.IsError, Is.True);
    }

    private static Pinned Depot(
        float x,
        float y,
        DateTimeOffset seen,
        (string, string, short)[] publicEntries,
        params (string Name, string Code, short Quantity)[] reserves
    ) =>
        new(
            "OriginHex",
            x,
            y,
            "StorageFacility",
            seen,
            publicEntries,
            [.. reserves.Select(r => (r.Name, new[] { ("Items", r.Code, r.Quantity) }))]
        );

    private async Task UploadAsync(ulong guild, params Pinned[] items)
    {
        using var client = _factory.CreateAuthorizedClient(
            guild: guild.ToString(),
            discordId: User.ToString()
        );
        using var response = await client.PostAsync(UploadPath, Body(MapDataWriter.File(items)));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private async Task SetLabelAsync(ulong guild, float x, string label)
    {
        using var db = _factory.CreateDb();
        var id = await db
            .Structures.Where(s => s.GuildId == (long)guild && s.X == x)
            .Select(s => s.StructureId)
            .SingleAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var change = await scope
            .ServiceProvider.GetRequiredService<StructureLabels>()
            .SetAsync((long)guild, FakeWarApi.War142.WarId, id, label, User, "test", default);
        Assert.That(change.Changed, Is.True, change.Refusal);
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        arguments["guild_id"] = Reader.ToString();
        using var http = _factory.CreateMcpClient(User.ToString());
        await using var client = await ConnectAsync(http);
        var result = await client.CallToolAsync(tool, arguments);
        Assert.That(result.IsError, Is.Not.True, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement;
    }
}
