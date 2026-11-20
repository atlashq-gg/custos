namespace Custos.Server.Storage;

// The tables of issue #1 ("Extension: guild storage in server/"). guild_id, then war_id, start each
// key of guild data. wars is the only table without guild_id.

/// <summary>Reference data from the Foxhole war API. Not guild data.</summary>
public sealed class War
{
    public required Guid WarId { get; init; }
    public required string Shard { get; init; }
    public required int WarNumber { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Set when the war API reports the next war.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }
}

/// <summary>One row for each accepted upload. The server does not keep the file.</summary>
public sealed class Upload
{
    public required long GuildId { get; init; }
    public required Guid WarId { get; init; }

    /// <summary>A UUID version 7.</summary>
    public required Guid UploadId { get; init; }

    /// <summary>The claim <c>sub</c> of the access token.</summary>
    public required string UserId { get; init; }

    /// <summary>The Discord user ID of the access token. Null for the rows of 0.1.0, and for a
    /// token without the claim when the Discord connection is off.</summary>
    public long? DiscordUserId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }
    public required byte[] Sha256 { get; init; }
    public required int SizeBytes { get; init; }
}

/// <summary>Current state. One row for each structure that a guild knows.</summary>
public sealed class Structure
{
    public required long GuildId { get; init; }
    public required Guid WarId { get; init; }

    /// <summary>See <see cref="EntityIds.Structure"/>.</summary>
    public required Guid StructureId { get; init; }

    /// <summary>The map name, for example <c>OriginHex</c>.</summary>
    public required string Region { get; init; }

    /// <summary><c>NormalizedMapCoords</c> as exact float32.</summary>
    public required float X { get; init; }

    public required float Y { get; init; }

    /// <summary>The structure type. A player can upgrade a structure, so it can change.</summary>
    public required string CodeName { get; set; }

    /// <summary>
    /// The label that the server generates for a new structure. It never changes. Null only for a
    /// row of 0.1.0 until <see cref="LabelBackfill"/> generates it.
    /// </summary>
    public string? GeneratedLabel { get; set; }

    /// <summary>The label that a user set. The label that shows is this label, or <see
    /// cref="GeneratedLabel"/> when it is null.</summary>
    public string? Label { get; set; }

    /// <summary><c>LastUpdated</c> of the state in this row.</summary>
    public required DateTimeOffset ObservedAt { get; set; }

    /// <summary>The latest <c>LastUpdated</c> with the same content.</summary>
    public required DateTimeOffset LastSeenAt { get; set; }

    public required byte[] ContentHash { get; set; }

    /// <summary>The members of the item details that have no table, as JSON.</summary>
    public required string Details { get; set; }
}

/// <summary>Current state. One row for each stockpile.</summary>
public sealed class Stockpile
{
    public required long GuildId { get; init; }
    public required Guid WarId { get; init; }

    /// <summary>See <see cref="EntityIds.Stockpile"/>.</summary>
    public required Guid StockpileId { get; init; }

    public required Guid StructureId { get; init; }

    /// <summary><c>null</c> for the public stockpile.</summary>
    public string? Name { get; init; }

    public required DateTimeOffset ObservedAt { get; set; }
    public required DateTimeOffset LastSeenAt { get; set; }
    public required byte[] ContentHash { get; set; }
}

/// <summary>Current state. The server replaces the entries of a stockpile when the stockpile
/// changes.</summary>
public sealed class StockpileEntry
{
    public required long GuildId { get; init; }
    public required Guid WarId { get; init; }
    public required Guid StockpileId { get; init; }

    /// <summary><c>item</c>, <c>item_crate</c>, <c>vehicle</c>, <c>vehicle_crate</c>,
    /// <c>structure</c>, or <c>structure_crate</c>.</summary>
    public required string Category { get; init; }

    public required string CodeName { get; init; }
    public required int Quantity { get; init; }
}

/// <summary>History. One row for each change of a stockpile. One partition for each war.</summary>
public sealed class StockpileChange
{
    public required long GuildId { get; init; }
    public required Guid WarId { get; init; }
    public required Guid StockpileId { get; init; }

    /// <summary><c>LastUpdated</c> of the first sighting of this state.</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary><c>LastUpdated</c> of the last sighting of this state.</summary>
    public required DateTimeOffset LastSeenAt { get; set; }

    public required Guid UploadId { get; init; }

    /// <summary>The entries by category, as JSON, for example <c>{"item_crate": {"RifleC":
    /// 20}}</c>.</summary>
    public required string Entries { get; init; }
}

/// <summary>History. One row for each change of a structure. One partition for each war.</summary>
public sealed class StructureChange
{
    public required long GuildId { get; init; }
    public required Guid WarId { get; init; }
    public required Guid StructureId { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; set; }
    public required Guid UploadId { get; init; }
    public required string CodeName { get; init; }
    public required string Details { get; init; }
}

/// <summary>
/// The settings document of a guild (<see cref="Discord.GuildSettings"/>). A guild without a row
/// has the default document.
/// </summary>
public sealed class GuildSettingsRow
{
    public required long GuildId { get; init; }

    /// <summary>The document as <c>jsonb</c>.</summary>
    public required string Document { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The Discord user ID of the last change.</summary>
    public required long UpdatedBy { get; set; }
}

/// <summary>Reference data: the map data of a region for one war, from the war API.</summary>
public sealed class MapRegion
{
    public required Guid WarId { get; init; }
    public required string Region { get; init; }

    /// <summary>The <c>ETag</c> of the static map data, for <c>If-None-Match</c> in the next
    /// war.</summary>
    public string? StaticEtag { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }
}

/// <summary>A place name of a region: an entry of <c>mapTextItems</c> of the static map
/// data.</summary>
public sealed class MapPlace
{
    public required Guid WarId { get; init; }
    public required string Region { get; init; }
    public required int PlaceIndex { get; init; }
    public required string Name { get; init; }
    public required float X { get; init; }
    public required float Y { get; init; }

    /// <summary><c>mapMarkerType</c> is <c>Major</c>.</summary>
    public required bool Major { get; init; }
}

/// <summary>A map item of a region: an entry of <c>mapItems</c> of the public dynamic map data. A
/// structure at its coordinates is a world structure.</summary>
public sealed class MapItem
{
    public required Guid WarId { get; init; }
    public required string Region { get; init; }
    public required int ItemIndex { get; init; }
    public required int IconType { get; init; }
    public required float X { get; init; }
    public required float Y { get; init; }
}
