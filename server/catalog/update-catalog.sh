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

# A vehicle or a structure has a crate size only when the Mass Production Factory (MPF) can produce
# it: the table Production has the flag IsMPFable for its recipe. A crate of such a vehicle or
# structure holds 3 (wiki page "Shippable Structures"), unless the wiki has another number
# (CrateCapacity of the recipe, or crate_amount of the table).
mpf_crate_size=3

# Table, the field with the category, the kind, and an extra field with its name in the row. The
# first table with a code name wins: an item before a vehicle before a structure.
#   pallet_size  the number of items in a pallet
tables=(
  "itemdata ItemCategory item pallet_amount=pallet_size"
  "vehicles type vehicle"
  "structures type structure"
)

# Builds the file from the rows of the three tables, in the order of the list above, and from the
# recipes of the vehicles and the structures ($production). group_by is stable, so the first row of
# a code name is the row of the first table.
read -r -d '' build <<'JQ' || true
def trim: gsub("^\\s+|\\s+$"; "");
def decode:
  gsub("&#(?<d>[0-9]+);"; "\(.d | tonumber | [.] | implode)")
  | gsub("&quot;"; "\"") | gsub("&lt;"; "<") | gsub("&gt;"; ">") | gsub("&amp;"; "&");
def number: (try tonumber catch null) | if . != null and . > 0 then . else null end;
def faction:
  ((. // "") | trim) as $f
  | if $f == "Col" then "colonial"
    elif $f == "War" then "warden"
    elif $f == "Both" or $f == "" then "both"
    else ($f | ascii_downcase) end;
# The recipes of a vehicle or a structure that the MPF can produce. The recipes name the output
# by its display name.
def mpf_recipes($kind; $display):
  [$production[] | select(.mpf and .type == $kind and ((.output // "") | trim | decode) == $display)];
[ .[]
  | ((.codename // "") | trim) as $code
  | ((.name // "") | trim | decode) as $display
  | select($code != "" and $display != "")
  | ((.category // "") | trim) as $category
  | ((.crate_size // "") | trim | number) as $table_crate
  | (if .kind == "item" then $table_crate
     else
       mpf_recipes(.kind; $display) as $recipes
       | if ($recipes | length) == 0 then null
         else ($table_crate
           // ([$recipes[] | (.capacity // "" | trim | number) | select(. != null)] | first)
           // $mpf_crate_size)
         end
     end) as $crate
  | ((.pallet_size // "") | trim | number) as $pallet
  | { code_name: $code,
      display_name: $display,
      kind: .kind,
      category: (if $category == "" then $code else $category end),
      faction: (.faction | faction) }
    + (if $crate then {crate_size: $crate} else {} end)
    + (if $pallet then {pallet_size: $pallet} else {} end) ]
| group_by(.code_name) | map(.[0])
| {source: $source, retrieved_at: $retrieved_at, entries: .}
JQ

# The Cargo API gives at most 500 rows for each request. Writes one JSON object for each row.
cargo() {
  local table=$1 fields=$2 where=$3 offset=0 page count
  while :; do
    page=$(curl --fail --silent --show-error --retry 3 --user-agent "custos-catalog/1.0" \
      --get "$api" \
      --data-urlencode "action=cargoquery" \
      --data-urlencode "format=json" \
      --data-urlencode "tables=$table" \
      --data-urlencode "fields=$fields" \
      --data-urlencode "where=$where" \
      --data-urlencode "limit=500" \
      --data-urlencode "offset=$offset")
    jq -c '.cargoquery[].title' <<<"$page"
    count=$(jq '.cargoquery | length' <<<"$page")
    ((count < 500)) && return 0
    offset=$((offset + 500))
  done
}

# The rows of one table, each with the fields codename, name, category, faction, crate_size, the
# extra field, and kind.
query() {
  local table=$1 category=$2 kind=$3 extra=${4:-}
  cargo "$table" "codename,name,$category=category,faction,crate_amount=crate_size${extra:+,$extra}" \
    "" | jq -c --arg kind "$kind" '. + {kind: $kind}'
}

# The recipes of the vehicles and the structures: the output, its type, if the MPF can produce it,
# and the crate capacity.
production() {
  cargo "Production" "Output,OutputType,IsMPFable,CrateCapacity" \
    "OutputType IN ('vehicle','structure')" \
    | jq -c '{output: .Output, type: .OutputType, mpf: (.IsMPFable == "1"), capacity: .CrateCapacity}'
}

for entry in "${tables[@]}"; do
  read -r table category kind extra <<<"$entry"
  query "$table" "$category" "$kind" "$extra"
done | jq -s --arg source "$source" --arg retrieved_at "$(date -u +%F)" \
  --argjson mpf_crate_size "$mpf_crate_size" --slurpfile production <(production) "$build"
