#!/usr/bin/env bash
# Writes the section "## <version>" of a changelog, without its heading, to a file. Fails when the
# changelog has no such section.
#
# Usage: release-notes.sh <changelog> <version> <output>
set -euo pipefail

changelog=$1
version=$2
output=$3

awk -v heading="## $version" \
  '$0 == heading { found = 1; next } found && /^## / { exit } found' \
  "$changelog" | sed '/./,$!d' > "$output"

if [ ! -s "$output" ]; then
  echo "$changelog has no section ## $version."
  exit 1
fi
