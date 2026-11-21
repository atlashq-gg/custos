# Catalog of the game

`catalog.json` gives each code name of the game a display name, a kind, a category, and a faction. The server uses it for the labels of the structures and for the searches. Without the file, each name and each category is the code name.

The file is not in the repository yet. The source is the Foxhole wiki, under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/). The Maintainer decides first if the file can be in this folder beside the code under AGPL-3.0-or-later ([#29](https://github.com/atlashq-gg/custos/issues/29)). When the file is added, this folder also gets the license text and a notice with the source, the date of the query, and the changes.

## Format

```json
{
  "source": "https://foxhole.wiki.gg/wiki/Special:CargoTables",
  "retrieved_at": "2026-11-20",
  "entries": [
    {
      "code_name": "StorageFacility",
      "display_name": "Storage Depot",
      "kind": "structure",
      "category": "Storage",
      "faction": "both"
    }
  ]
}
```

| Member | Content |
|---|---|
| `code_name` | The code name in the map data file. The server compares it without regard to case. |
| `display_name` | The name that a player reads. For a structure, the label leaves out a tier such as ` (Tier 2)`. |
| `kind` | `item`, `vehicle`, or `structure`. |
| `category` | `ItemCategory` of the wiki table `itemdata`, or `type` of the tables `vehicles` and `structures`. |
| `faction` | `colonial`, `warden`, or `both`. |

## Update

Run the script after a game update. It needs the .NET SDK 10 and access to the wiki.

```sh
cd server/catalog
dotnet run update-catalog.cs -- catalog.json
```

The script queries the Cargo tables `itemdata`, `vehicles`, and `structures` through `api.php?action=cargoquery`. The queries are in the script, so that each run gives the same shape. A code name that is in more than one table gets the entry of the first table in that order.

On 2026-11-20 the script wrote 662 entries: 245 items, 176 vehicles, and 241 structures. The code names of the sample of 0.1.0 (`StorageFacility`, `Seaport`, `Refinery`, `FortBaseT2`, `MaintenanceTunnel`, `RifleC`, `TruckC`) were all in the tables.

The server reads the file from `Custos:CatalogPath` (default `catalog/catalog.json` in the folder of the server). The build and the image copy the file when it exists.
