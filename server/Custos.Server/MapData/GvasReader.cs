using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Custos.Server.MapData;

/// <summary>The file is not a GVAS file that the server can read.</summary>
public sealed class GvasFormatException(string message) : Exception(message);

/// <summary>The class of the save game and its properties.</summary>
public sealed record GvasFile(string SaveGameClass, JsonObject Properties);

/// <summary>
/// Reads an Unreal Engine 4 save file (GVAS) into a JSON tree. A struct becomes an object, an array an array,
/// and an enum the string that the file holds, for example <c>EWorldConquestMapId::OriginHex</c>. A property
/// or struct that the reader does not know is left out: the reader skips it by its size.
/// </summary>
internal sealed class GvasReader
{
    private const int MaxDepth = 32;
    private static ReadOnlySpan<byte> Magic => "GVAS"u8;

    private readonly ReadOnlyMemory<byte> _data;
    private int _position;
    private int _depth;

    private GvasReader(ReadOnlyMemory<byte> data) => _data = data;

    private ReadOnlySpan<byte> Span => _data.Span;

    public static GvasFile Read(ReadOnlyMemory<byte> data)
    {
        var reader = new GvasReader(data);
        if (!reader.Take(Magic.Length).SequenceEqual(Magic)) throw new GvasFormatException("The file is not a GVAS file.");

        var saveGameVersion = reader.Int32();
        reader.Skip(saveGameVersion >= 3 ? 8 : 4); // The package versions of UE4, and of UE5 from version 3.
        reader.Skip(2 + 2 + 2 + 4); // The engine version: major, minor, patch, changelist.
        reader.String(); // The branch.
        reader.Skip(4); // The format of the custom versions.
        reader.Skip(reader.Count(20) * 20); // The custom versions: a GUID and a version each.
        var saveGameClass = reader.String();

        return new GvasFile(saveGameClass, reader.Properties());
    }

    /// <summary>A list of properties up to the name <c>None</c>.</summary>
    private JsonObject Properties()
    {
        if (++_depth > MaxDepth) throw new GvasFormatException("The properties are nested too deep.");

        var properties = new JsonObject();
        while (String() is var name and not "None")
        {
            var type = String();
            var size = Size();
            var value = PropertyValue(type, size);
            if (value is not null) properties[name] = value;
        }

        _depth--;
        return properties;
    }

    private JsonNode? PropertyValue(string type, int size)
    {
        switch (type)
        {
            case "StructProperty":
            {
                var structName = String();
                Skip(16); // The GUID of the struct.
                PropertyGuid();
                return Bounded(size, () => StructBody(structName));
            }
            case "ArrayProperty":
            {
                var innerType = String();
                PropertyGuid();
                return Bounded(size, () => ArrayBody(innerType, size));
            }
            case "ByteProperty":
            {
                var enumName = String();
                PropertyGuid();
                return enumName == "None" ? JsonValue.Create((int)Take(1)[0]) : JsonValue.Create(String());
            }
            case "EnumProperty":
                String(); // The enum name. The value repeats it.
                PropertyGuid();
                return JsonValue.Create(String());
            case "BoolProperty":
            {
                var value = Take(1)[0] != 0;
                PropertyGuid();
                return JsonValue.Create(value);
            }
            default:
                PropertyGuid();
                return Bounded(size, () => Scalar(type));
        }
    }

    private JsonNode? ArrayBody(string innerType, int size)
    {
        var count = Count(1);
        var array = new JsonArray();

        switch (innerType)
        {
            case "StructProperty":
                String(); // The name of the array.
                String(); // The type: StructProperty.
                Size();
                var structName = String();
                Skip(16);
                PropertyGuid();
                for (var i = 0; i < count; i++) array.Add(StructBody(structName));
                return array;
            case "ByteProperty" when size - 4 == count: // Raw bytes, not enum names.
                foreach (var b in Take(count)) array.Add(JsonValue.Create((int)b));
                return array;
            case "ByteProperty" or "EnumProperty":
                for (var i = 0; i < count; i++) array.Add(JsonValue.Create(String()));
                return array;
            case "BoolProperty":
                foreach (var b in Take(count)) array.Add(JsonValue.Create(b != 0));
                return array;
            default:
                for (var i = 0; i < count; i++)
                {
                    if (Scalar(innerType) is not { } value) return null;
                    array.Add(value);
                }
                return array;
        }
    }

    private JsonNode? StructBody(string structName) => structName switch
    {
        "Vector2D" => new JsonObject { ["x"] = Single(), ["y"] = Single() },
        "Vector" => new JsonObject { ["x"] = Single(), ["y"] = Single(), ["z"] = Single() },
        "DateTime" => JsonValue.Create(Ticks()),
        "Guid" => JsonValue.Create(new Guid(Take(16))),
        _ => Properties(),
    };

    /// <summary>A value of fixed size, or <c>null</c> for a type that the reader does not know.</summary>
    private JsonNode? Scalar(string type) => type switch
    {
        "IntProperty" => Int32(),
        "Int16Property" => (int)BinaryPrimitives.ReadInt16LittleEndian(Take(2)),
        "UInt16Property" => (int)BinaryPrimitives.ReadUInt16LittleEndian(Take(2)),
        "UInt32Property" => BinaryPrimitives.ReadUInt32LittleEndian(Take(4)),
        "Int64Property" => BinaryPrimitives.ReadInt64LittleEndian(Take(8)),
        "FloatProperty" => Single(),
        "DoubleProperty" => BinaryPrimitives.ReadDoubleLittleEndian(Take(8)),
        "StrProperty" or "NameProperty" => String(),
        _ => null,
    };

    /// <summary>
    /// Reads a value that has a known size. If the reader does not know the value, or the value does not fill
    /// the size, the reader continues after the value and gives <c>null</c>.
    /// </summary>
    private JsonNode? Bounded(int size, Func<JsonNode?> read)
    {
        var end = _position + size;
        if (end > Span.Length) throw new GvasFormatException("A property is larger than the file.");

        var depth = _depth;
        JsonNode? value;
        try
        {
            value = read();
        }
        catch (GvasFormatException)
        {
            value = null;
            _depth = depth;
        }

        if (_position != end) value = null;
        _position = end;
        return value;
    }

    private void PropertyGuid()
    {
        if (Take(1)[0] != 0) Skip(16);
    }

    private DateTimeOffset Ticks()
    {
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new GvasFormatException("A date is out of range.");
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>An FString: a length, then Latin-1 for a positive length or UTF-16 for a negative length.</summary>
    private string String()
    {
        var length = Int32();
        if (length == 0) return "";

        var (bytes, encoding) = length > 0 ? (length, Encoding.Latin1) : (checked(-length * 2), Encoding.Unicode);
        var nul = length > 0 ? 1 : 2;
        return encoding.GetString(Take(bytes)[..^nul]);
    }

    /// <summary>A count of elements that are each at least <paramref name="minBytes"/> long.</summary>
    private int Count(int minBytes)
    {
        var count = Int32();
        if (count < 0 || (long)count * minBytes > Span.Length - _position) throw new GvasFormatException("A count is out of range.");
        return count;
    }

    private int Size()
    {
        var size = BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        if (size < 0 || size > Span.Length - _position) throw new GvasFormatException("A size is out of range.");
        return (int)size;
    }

    private int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    private float Single() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    private void Skip(int count) => Take(count);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Span.Length - _position) throw new GvasFormatException("The file ends too early.");
        var span = Span.Slice(_position, count);
        _position += count;
        return span;
    }
}
