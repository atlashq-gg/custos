# Catalog license

`catalog.json` is not under the AGPL-3.0-or-later license of the server.

- Source: the Cargo tables `itemdata`, `vehicles`, and `structures` of the Foxhole wiki, https://foxhole.wiki.gg/wiki/Special:CargoTables. Queried on 2026-11-21 with `update-catalog.sh`.
- Author: the contributors of the Foxhole wiki. Foxhole is a game by Siege Camp.
- License: Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0), https://creativecommons.org/licenses/by-nc-sa/4.0/. The wiki states this license in its site information (`api.php?action=query&meta=siteinfo&siprop=rightsinfo`). The full text is in [CC-BY-NC-SA-4.0.txt](CC-BY-NC-SA-4.0.txt).
- Changes: each entry has only the code name, the display name, the kind, the category, and the faction. The kind is the table of the entry. The category is `ItemCategory` for an item and `type` for a vehicle and a structure. The faction values `Col` and `War` are `colonial` and `warden`, and `Both` or an empty value is `both`. HTML entities in a name are decoded. A code name in more than one table has the entry of the first table in the order items, vehicles, structures. The file is sorted by code name and its members are in snake_case.

Custos is not made or endorsed by Siege Camp or by the Foxhole wiki.

The ShareAlike term applies to an adaptation of this file: it stays under CC BY-NC-SA 4.0. The NonCommercial term applies to each package that holds this file, for example the container image of the server. Nobody can sell such a package or use it for a commercial purpose. To remove that limit, build the image without `catalog/catalog.json`: each name is then the code name.
