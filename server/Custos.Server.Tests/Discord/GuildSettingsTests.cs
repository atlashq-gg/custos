using Custos.Server.Discord;

namespace Custos.Server.Tests.Discord;

public sealed class GuildSettingsTests
{
    private const ulong GuildId = 300000000000000000;

    [Test]
    public void Default_document_has_empty_lists()
    {
        Assert.That(
            GuildSettings.Default.ToJson(),
            Is.EqualTo(
                """
                {
                  "SchemaVersion": 1,
                  "Access": {
                    "ReportRoleIds": [],
                    "ReadRoleIds": [],
                    "LabelRoleIds": []
                  },
                  "Sharing": {
                    "ReadGuildIds": []
                  }
                }
                """.ReplaceLineEndings("\n")
            )
        );
    }

    [Test]
    public void Document_is_read_with_ids_as_strings()
    {
        var result = GuildSettings.Parse(
            """
            {
              "SchemaVersion": 1,
              "Access": {
                "ReportRoleIds": [],
                "ReadRoleIds": ["111111111111111111", "111111111111111111"],
                "LabelRoleIds": ["18446744073709551615"]
              },
              "Sharing": { "ReadGuildIds": ["333333333333333333"] }
            }
            """,
            GuildId
        );

        Assert.That(result.Refusal, Is.Null);
        var settings = result.Settings!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.Access.ReadRoleIds, Is.EqualTo(new[] { 111111111111111111UL }));
            Assert.That(settings.Access.LabelRoleIds, Is.EqualTo(new[] { ulong.MaxValue }));
            Assert.That(settings.Sharing.ReadGuildIds, Is.EqualTo(new[] { 333333333333333333UL }));
            Assert.That(
                GuildSettings.Parse(settings.ToJson(), GuildId).Settings,
                Is.EqualTo(settings).Using<GuildSettings>((a, b) => a.ToJson() == b.ToJson())
            );
        }
    }

    [Test]
    public void Missing_member_has_its_default() =>
        Assert.That(
            GuildSettings.Parse("""{ "SchemaVersion": 1 }""", GuildId).Settings?.ToJson(),
            Is.EqualTo(GuildSettings.Default.ToJson())
        );

    [TestCase("""{ "SchemaVersion": 2 }""", "SchemaVersion must be 1")]
    [TestCase("""{ "Access": {} }""", "SchemaVersion must be 1")]
    [TestCase("""{ "SchemaVersion": "1" }""", "SchemaVersion must be 1")]
    [TestCase("not json", "not valid")]
    [TestCase("""{ "SchemaVersion": 1, "Acess": {} }""", "not valid")]
    [TestCase("""{ "SchemaVersion": 1, "Access": null }""", "not valid")]
    [TestCase(
        """{ "SchemaVersion": 1, "Access": { "ReadRoleIds": [111111111111111111] } }""",
        "Not an ID: 111111111111111111"
    )]
    [TestCase(
        """{ "SchemaVersion": 1, "Access": { "ReadRoleIds": ["1234"] } }""",
        "Not an ID: \"1234\""
    )]
    [TestCase(
        """{ "SchemaVersion": 1, "Access": { "ReadRoleIds": ["99999999999999999999"] } }""",
        "Not an ID"
    )]
    [TestCase(
        """{ "SchemaVersion": 1, "Sharing": { "ReadGuildIds": ["300000000000000000"] } }""",
        "Sharing.ReadGuildIds must not hold this guild"
    )]
    public void Document_is_refused(string json, string reason) =>
        Assert.That(GuildSettings.Parse(json, GuildId).Refusal, Does.Contain(reason));

    [Test]
    public void List_with_more_than_25_entries_is_refused()
    {
        var ids = string.Join(
            ", ",
            Enumerable.Range(0, 26).Select(i => $"\"{100000000000000000 + i}\"")
        );

        Assert.That(
            GuildSettings
                .Parse(
                    $$"""{ "SchemaVersion": 1, "Access": { "LabelRoleIds": [{{ids}}] } }""",
                    GuildId
                )
                .Refusal,
            Does.Contain("Access.LabelRoleIds has 26 entries. The limit is 25.")
        );
    }
}
