#!/usr/bin/env bash
# Makes catalog.json from the Cargo tables of the Foxhole wiki. Run it from this folder after a
# game update:
#
#   ./update-catalog.sh [catalog.json]
#
# It needs curl and jq. The queries are in this file, so that each run gives the same shape. The
# wiki content is under CC BY-NC-SA 4.0 (LICENSE.md): change the date in LICENSE.md with the file.
set -euo pipefail

api="https://foxhole.wiki.gg/api.php"
source="https://foxhole.wiki.gg/wiki/Special:CargoTables"
output="${1:-catalog.json}"

# Table, the field with the category, and the kind. The first table with a code name wins: an
# item before a vehicle before a structure.
tables=(
  "itemdata ItemCategory item"
  "vehicles type vehicle"
  "structures type structure"
)

# Builds the file from the rows of the three tables, in the order of the list above. group_by is
# stable, so the first row of a code name is the row of the first table.
read -r -d '' build <<'JQ' || true
def trim: gsub("^\\s+|\\s+$"; "");
def decode:
  gsub("&#(?<d>[0-9]+);"; "\(.d | tonumber | [.] | implode)")
  | gsub("&quot;"; "\"") | gsub("&lt;"; "<") | gsub("&gt;"; ">") | gsub("&amp;"; "&");
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
  | { code_name: $code,
      display_name: ($name | decode),
      kind: .kind,
      category: (if $category == "" then $code else $category end),
      faction: (.faction | faction) } ]
| group_by(.code_name) | map(.[0])
| {source: $source, retrieved_at: $retrieved_at, entries: .}
JQ

# The Cargo API gives at most 500 rows for each request. Writes the rows of one table, each with
# the fields codename, name, category, faction, and kind.
query() {
  local table=$1 category=$2 kind=$3 offset=0 page count
  while :; do
    page=$(curl --fail --silent --show-error --retry 3 --user-agent "custos-catalog/1.0" \
      --get "$api" \
      --data-urlencode "action=cargoquery" \
      --data-urlencode "format=json" \
      --data-urlencode "tables=$table" \
      --data-urlencode "fields=codename,name,$category=category,faction" \
      --data-urlencode "limit=500" \
      --data-urlencode "offset=$offset")
    jq -c --arg kind "$kind" '.cargoquery[].title + {kind: $kind}' <<<"$page"
    count=$(jq '.cargoquery | length' <<<"$page")
    ((count < 500)) && return 0
    offset=$((offset + 500))
  done
}

for entry in "${tables[@]}"; do
  read -r table category kind <<<"$entry"
  query "$table" "$category" "$kind"
done | jq -s --arg source "$source" --arg retrieved_at "$(date -u +%F)" "$build" >"$output"

echo "$(jq '.entries | length' "$output") entries in $output"
