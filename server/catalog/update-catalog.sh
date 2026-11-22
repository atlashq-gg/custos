#!/usr/bin/env bash
# Writes the catalog, as JSON, to the standard output. It makes the catalog from the Cargo tables of
# the Foxhole wiki. Run it from this folder after a game update:
#
#   ./update-catalog.sh > catalog.json
#
# It needs curl and jq. The queries are in this file, so that each run gives the same shape. The
# wiki content is under CC BY-NC-SA 4.0 (LICENSE.md): change the date in LICENSE.md with the file.
set -euo pipefail

api="https://foxhole.wiki.gg/api.php"
source="https://foxhole.wiki.gg/wiki/Special:CargoTables"

# A shippable crate holds 3 packed vehicles or structures (wiki page "Shippable Structures"). The
# tables have the number for one vehicle only, so a shippable entry without a number gets this one.
# A vehicle or a structure that is not shippable has no crate size, also when the wiki has a number.
shippable_crate_size=3

# Table, the field with the category, the kind, and the extra field with its name in the row. The
# first table with a code name wins: an item before a vehicle before a structure.
#   pallet_size  the number of items in a pallet
#   shippable    the vehicle size or the construction type that says if the entry is shippable
tables=(
  "itemdata ItemCategory item pallet_amount=pallet_size"
  "vehicles type vehicle shippable_size=shippable"
  "structures type structure construction_type=shippable"
)

# Builds the file from the rows of the three tables, in the order of the list above. group_by is
# stable, so the first row of a code name is the row of the first table.
read -r -d '' build <<'JQ' || true
def trim: gsub("^\\s+|\\s+$"; "");
def decode:
  gsub("&#(?<d>[0-9]+);"; "\(.d | tonumber | [.] | implode)")
  | gsub("&quot;"; "\"") | gsub("&lt;"; "<") | gsub("&gt;"; ">") | gsub("&amp;"; "&");
def shippable:
  ((.shippable // "") | trim) as $s
  | if .kind == "vehicle" then ($s == "Medium" or $s == "Large" or $s == "Extra Large")
    elif .kind == "structure" then $s == "Shippable"
    else false end;
def number($field):
  ((.[$field] // "") | trim | try tonumber catch null)
  | if . != null and . > 0 then . else null end;
def faction:
  ((. // "") | trim) as $f
  | if $f == "Col" then "colonial"
    elif $f == "War" then "warden"
    elif $f == "Both" or $f == "" then "both"
    else ($f | ascii_downcase) end;
[ .[]
  | ((.codename // "") | trim) as $code
  | ((.name // "") | trim) as $name
  | select($code != "" and $name != "")
  | ((.category // "") | trim) as $category
  | (if .kind == "item" then number("crate_size")
     elif shippable then (number("crate_size") // $shippable_crate_size)
     else null end) as $crate
  | number("pallet_size") as $pallet
  | { code_name: $code,
      display_name: ($name | decode),
      kind: .kind,
      category: (if $category == "" then $code else $category end),
      faction: (.faction | faction) }
    + (if $crate then {crate_size: $crate} else {} end)
    + (if $pallet then {pallet_size: $pallet} else {} end) ]
| group_by(.code_name) | map(.[0])
| {source: $source, retrieved_at: $retrieved_at, entries: .}
JQ

# The Cargo API gives at most 500 rows for each request. Writes the rows of one table, each with
# the fields codename, name, category, faction, crate_size, the extra field, and kind.
query() {
  local table=$1 category=$2 kind=$3 extra=$4 offset=0 page count
  while :; do
    page=$(curl --fail --silent --show-error --retry 3 --user-agent "custos-catalog/1.0" \
      --get "$api" \
      --data-urlencode "action=cargoquery" \
      --data-urlencode "format=json" \
      --data-urlencode "tables=$table" \
      --data-urlencode "fields=codename,name,$category=category,faction,crate_amount=crate_size,$extra" \
      --data-urlencode "limit=500" \
      --data-urlencode "offset=$offset")
    jq -c --arg kind "$kind" '.cargoquery[].title + {kind: $kind}' <<<"$page"
    count=$(jq '.cargoquery | length' <<<"$page")
    ((count < 500)) && return 0
    offset=$((offset + 500))
  done
}

for entry in "${tables[@]}"; do
  read -r table category kind extra <<<"$entry"
  query "$table" "$category" "$kind" "$extra"
done | jq -s --arg source "$source" --arg retrieved_at "$(date -u +%F)" \
  --argjson shippable_crate_size "$shippable_crate_size" "$build"
