using System.Text;
using System.Text.RegularExpressions;

namespace Custos.Server.Storage;

/// <summary>
/// Generates the label of a structure. It has no I/O.
/// <list type="bullet">
/// <item>A world structure gets the place and the type, for example "Fort Barley Seaport".</item>
/// <item>A player-built structure gets the place, the type, and a code, for example "Fort Barley
/// Bunker Base K7Q2".</item>
/// </list>
/// The place is the nearest place name of the region. The code is 4 characters of the structure
/// ID in Crockford base32, so each guild generates the same label for one structure. If the label
/// exists in the guild, the generator adds the code, or more characters of the code.
/// </summary>
internal static partial class LabelGenerator
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeLength = 4;

    /// <summary>The largest distance between a structure and a map item of the same
    /// position.</summary>
    private const float SamePosition = 1e-5f;

    /// <summary>The first <paramref name="length"/> characters of the ID in Crockford
    /// base32.</summary>
    public static string Code(Guid structureId, int length)
    {
        var bytes = structureId.ToByteArray(bigEndian: true);
        var code = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            // 5 bits for each character, from the start of the ID.
            var bit = i * 5;
            var value = 0;
            for (var b = 0; b < 5; b++)
            {
                var index = bit + b;
                var set = index < 128 && (bytes[index / 8] & (0x80 >> (index % 8))) != 0;
                value = (value << 1) | (set ? 1 : 0);
            }
            code.Append(Crockford[value]);
        }
        return code.ToString();
    }

    /// <summary>The name of the nearest place, or <c>null</c> without places.</summary>
    public static string? Place(IReadOnlyList<MapPlace> places, float x, float y) =>
        places.MinBy(p => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y))?.Name;

    /// <summary>A structure is a world structure when the war API lists a map item at its
    /// coordinates.</summary>
    public static bool IsWorldStructure(IReadOnlyList<MapItem> items, float x, float y) =>
        items.Any(i => MathF.Abs(i.X - x) <= SamePosition && MathF.Abs(i.Y - y) <= SamePosition);

    /// <summary>A region name to read, for example "The Fingers" for
    /// <c>TheFingersHex</c>.</summary>
    public static string RegionName(string region) =>
        CamelCase().Replace(HexSuffix().Replace(region, ""), "$1 $2");

    /// <summary>The first label that <paramref name="taken"/> does not refuse.</summary>
    public static string Generate(
        string place,
        string type,
        bool world,
        Guid structureId,
        Func<string, bool> taken
    )
    {
        var baseLabel = $"{place} {type}";
        if (world && !taken(baseLabel))
            return baseLabel;

        for (var length = CodeLength; ; length++)
        {
            var label = $"{baseLabel} {Code(structureId, length)}";
            if (!taken(label) || length >= 26)
                return label;
        }
    }

    [GeneratedRegex("Hex$")]
    private static partial Regex HexSuffix();

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelCase();
}
