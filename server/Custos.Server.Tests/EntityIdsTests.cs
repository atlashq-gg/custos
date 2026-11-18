using Custos.Server.Storage;

namespace Custos.Server.Tests;

public sealed class EntityIdsTests
{
    private static readonly Guid War = Guid.Parse("1bb12ec2-e62d-4e77-a218-5c729e5b608e");

    [Test]
    public void Uuid_version_5_matches_the_rfc_algorithm()
    {
        // uuid.uuid5(uuid.NAMESPACE_DNS, "python.org") in Python.
        var dns = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.That(
            EntityIds.Create(dns, "python.org"),
            Is.EqualTo(Guid.Parse("886313e1-3b8a-5372-9b90-0c9aee199e5d"))
        );
    }

    [Test]
    public void Structure_id_depends_on_each_part_of_the_key()
    {
        var id = EntityIds.Structure("live", War, "OriginHex", 0.30871195f, 0.7751747f);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                EntityIds.Structure("live", War, "OriginHex", 0.30871195f, 0.7751747f),
                Is.EqualTo(id)
            );
            Assert.That(
                EntityIds.Structure("live-2", War, "OriginHex", 0.30871195f, 0.7751747f),
                Is.Not.EqualTo(id)
            );
            Assert.That(
                EntityIds.Structure("live", Guid.NewGuid(), "OriginHex", 0.30871195f, 0.7751747f),
                Is.Not.EqualTo(id)
            );
            Assert.That(
                EntityIds.Structure("live", War, "AshFieldsHex", 0.30871195f, 0.7751747f),
                Is.Not.EqualTo(id)
            );
            Assert.That(
                EntityIds.Structure(
                    "live",
                    War,
                    "OriginHex",
                    MathF.BitIncrement(0.30871195f),
                    0.7751747f
                ),
                Is.Not.EqualTo(id)
            );
        }
    }

    [Test]
    public void Reserve_stockpile_named_public_differs_from_the_public_stockpile()
    {
        var structure = EntityIds.Structure("live", War, "OriginHex", 0.5f, 0.5f);

        Assert.That(
            EntityIds.Stockpile(structure, "public"),
            Is.Not.EqualTo(EntityIds.Stockpile(structure, null))
        );
    }
}
