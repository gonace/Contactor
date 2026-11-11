#!/usr/bin/env bash
# Prints the next release version: today's date in UTC as YYYY.M.D, without leading zeros, e.g. 2026.11.4. When the
# tag v<date> is already on origin, a release was made today: it adds the next counter, e.g. 2026.11.4.1, then .2.
# Used by the Images workflow and scripts/images-publish.sh, which tag the commit they publish v<version>.
#
#   bash scripts/release-version.sh
set -euo pipefail

IFS=. read -r year month day <<<"$(date -u +%Y.%m.%d)"
# 10# reads "08" as decimal and drops the leading zero, like .NET's version does.
date="$year.$((10#$month)).$((10#$day))"

tags=$(git ls-remote --tags --refs origin "refs/tags/v$date*") || { echo "Error: could not list the tags on origin." >&2; exit 1; }
awk -v date="$date" '
    { tag = $2; sub("^refs/tags/v", "", tag) }
    tag == date { released = 1 }
    index(tag, date ".") == 1 {
        counter = substr(tag, length(date) + 2)
        if (counter ~ /^[0-9]+$/) { released = 1; if (counter + 0 > highest) { highest = counter + 0 } }
    }
    END { print released ? date "." (highest + 1) : date }
' <<<"$tags"
