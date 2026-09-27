#!/usr/bin/env bash
# Island sweep: is the fragmentation caused by the walkable-area filters, or is the zone really
# made of hundreds of small walkable pieces?
#
# `Mnemosyne.Cli build` writes its result over %APPDATA%\Mnemosyne\built\<zone>.navmesh by default
# - the store the running service serves from. This sweeps into scratch/ instead: every run gets its
# own output directory and its own log, and the live mesh is only ever read as a reference.
#
# What to look at: island count and the largest island's share of walkable area, per configuration.
#   - a filter that, when cleared, collapses the island count is over-trimming the mesh;
#   - if no single filter moves it much, the zone genuinely fragments and the answer is
#     connectivity (links/merge), not more filtering.
#
# usage: bash tools/island-sweep.sh [zone-substring] [--quick]
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
to_win() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

zone="${1:-roc_r1f1}"
quick="${2:-}"
cli="$(to_win "$here/src/Mnemosyne.Cli/bin/Release/net10.0/Mnemosyne.Cli.exe")"
out="$(to_win "$here/scratch/sweep-$zone")"
mkdir -p "$out"

if [ ! -x "$cli" ]; then
  echo "no CLI at $cli — run: dotnet build Mnemosyne.slnx -c Release" >&2
  exit 1
fi

echo "zone: $zone"
echo "out:  $out"
echo

run() {
  local name="$1"; shift
  local log="$out/$name.log"
  local t0=$SECONDS
  printf '%-16s ' "$name"
  if "$cli" build "$zone" "--out=$out/$name" "$@" > "$log" 2>&1; then
    local secs=$((SECONDS - t0))
    grep -E "^built in |^islands " "$log" | sed 's/^/  /' || true
    printf '  (%ss)\n' "$secs"
  else
    echo "FAILED — see $log (tail:)"
    tail -3 "$log" | sed 's/^/  /'
  fi
}

run baseline
run no-ledge --clear=LedgeSpans
run no-lowhanging --clear=LowHangingObstacles
if [ "$quick" != "--quick" ]; then
  run no-lowheight --clear=WalkableLowHeightSpans
  run no-interiors --clear=Interiors
  run no-filters --clear=all
  run cell-0.5 --cell=0.5
fi

echo
echo "logs in $out; summarize with tools/island-sweep-report.py"
