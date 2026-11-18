using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Custos.Server.Storage;

/// <summary>
/// The IDs of the entities in the world. An ID is a UUID version 5 of the key of the entity, so each guild
/// calculates the same ID from its own data. Do not change the namespace or the names: a change gives new IDs
/// to all entities.
/// </summary>
internal static class EntityIds
{
    public static readonly Guid Namespace = new("9794ad62-0ba5-4bf8-bbe8-2d72369cf844");

    /// <summary>The key is the shard, the war, the region, and the coordinates as exact float32.</summary>
    public static Guid Structure(string shard, Guid warId, string region, float x, float y) =>
        Create(Namespace, string.Join('\n', "structure", shard, warId.ToString("D"), region, Bits(x), Bits(y)));

    /// <summary>The key is the structure, then <c>public</c>, or <c>reserve</c> and the name of the reserve stockpile.</summary>
    public static Guid Stockpile(Guid structureId, string? name) =>
        Create(Namespace, name is null
            ? string.Join('\n', "stockpile", structureId.ToString("D"), "public")
            : string.Join('\n', "stockpile", structureId.ToString("D"), "reserve", name));

    /// <summary>A UUID version 5 (RFC 9562): SHA-1 of the namespace and the UTF-8 name.</summary>
    public static Guid Create(Guid ns, string name)
    {
        var input = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        ns.TryWriteBytes(input, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name, input.AsSpan(16));

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    private static string Bits(float value) => BitConverter.SingleToUInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);
}
