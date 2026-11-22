# Catalog of the game

`catalog.json` gives each code name of the game a display name, a kind, a category, and a faction. The server uses it for the labels of the structures and for the searches. Without the file, each name and each category is the code name.

The source is the Foxhole wiki. The file is under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/), not under the AGPL-3.0-or-later license of the rest of `server/`. [LICENSE.md](LICENSE.md) has the source, the date of the query, and the changes. [CC-BY-NC-SA-4.0.txt](CC-BY-NC-SA-4.0.txt) has the license text.

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
    },
    {
      "code_name": "Cloth",
      "display_name": "Basic Materials",
      "kind": "item",
      "category": "Supplies",
      "faction": "both",
      "crate_size": 100
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
| `crate_size` | The number in one crate. For an item, the number in an item crate (`crate_amount` of the wiki). For a vehicle or a structure, the number in a shippable crate: 3 (see below). The member is not there when the catalog has no number. |
| `pallet_size` | The number of items in a pallet (`pallet_amount` of the wiki). Only an item has it. The member is not there when the wiki has no number. |

A shippable crate holds 3 packed vehicles or structures (wiki page "Shippable Structures"). The wiki tables do not have that number, except for one vehicle. The script sets `crate_size` to 3 for each shippable entry that has no number of its own:

- A vehicle is shippable when `shippable_size` of the wiki is `Medium`, `Large`, or `Extra Large`. `Unpackageable` and an empty size are not.
- A structure is shippable when `construction_type` of the wiki is `Shippable`.

A number in the wiki wins for a shippable entry. `MediumTankC` has 5 in the wiki, and the Maintainer confirmed it, so it has 5, not 3. A vehicle or a structure that is not shippable has no crate size, also when the wiki has a number.

## Update

Run the script after a game update. It needs `curl`, `jq`, and access to the wiki. Change the date in [LICENSE.md](LICENSE.md) in the same commit.

```sh
cd server/catalog
./update-catalog.sh > catalog.json
```

The script writes the catalog to the standard output, so a pipe to `jq` also works. The shell empties `catalog.json` before the script starts. If the script fails, it exits with a code other than `0`: restore the file with `git restore catalog.json`.

The script queries the Cargo tables `itemdata`, `vehicles`, and `structures` through `api.php?action=cargoquery`. The queries are in the script, so that each run gives the same shape. A code name that is in more than one table gets the entry of the first table in that order.

On 2026-11-22 the script wrote 688 entries: 253 items, 189 vehicles, and 246 structures. The code names of the sample of 0.1.0 (`StorageFacility`, `Seaport`, `Refinery`, `FortBaseT2`, `MaintenanceTunnel`, `RifleC`, `TruckC`) were all in the tables.

The server reads the file from `Custos:CatalogPath` (default `catalog/catalog.json` in the folder of the server). The build and the image copy the file with `LICENSE.md` and the license text.
