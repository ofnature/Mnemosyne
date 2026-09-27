#!/usr/bin/env python3
"""Summarize island-sweep logs into one table.

Reads the logs written by tools/island-sweep.sh and prints, per configuration, the built mesh's
poly/tile count, its island count and the largest island's share of walkable area - against the
reference build of the same zone, which is what the live store holds.

Read it like this:
  * a configuration that clears a filter and collapses the island count is removing over-trimming;
  * a configuration that keeps the island count is only removing mesh, which is not the same win;
  * if nothing moves the number much, the zone really is made of many small walkable pieces, and
    the fix is connectivity (off-mesh links / region merge), not filter tuning.

usage: python tools/island-sweep-report.py [scratch/sweep-<zone>]
"""
import re
import sys
import pathlib

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "scratch/sweep-roc_r1f1")
if not root.exists():
    sys.exit(f"no sweep logs at {root} - run: bash tools/island-sweep.sh")

built = re.compile(r"built in (?P<secs>[\d.]+) s: tiles (?P<tiles>\d+) \(ref (?P<rtiles>\d+)\), "
                   r"polys (?P<polys>\d+) \(ref (?P<rpolys>\d+)\)")
islands = re.compile(r"^islands\s+(?P<which>built|ref): (?P<n>\d+) "
                     r"\(largest (?P<share>[\d.]+)% of (?P<area>\d+) m2\)", re.M)

rows = []
reference = None
for log in sorted(root.glob("*.log")):
    text = log.read_text(errors="replace")
    b = built.search(text)
    found = {m["which"]: m for m in islands.finditer(text)}
    if not b or "built" not in found:
        rows.append((log.stem, None, None, None, None, "no parse"))
        continue
    isl, ref = found["built"], found.get("ref")
    if ref:
        reference = (int(ref["n"]), float(ref["share"]))
    rows.append((log.stem, int(b["polys"]), int(b["tiles"]), int(isl["n"]),
                 float(isl["share"]), None))

ok = [r for r in rows if r[1] is not None]
if not ok:
    for r in rows:
        print(f"{r[0]:16} {r[5]}")
    sys.exit(1)

base = next((r for r in ok if r[0] == "baseline"), ok[0])
print(f"{'config':16} {'polys':>8} {'vs base':>9} {'tiles':>6} {'islands':>8} {'vs base':>9} {'largest':>8}")
for name, polys, tiles, isl, share, _ in sorted(ok, key=lambda r: r[3]):
    dp = f"{(polys / base[1] - 1) * 100:+.1f}%" if base[1] else "-"
    di = f"{isl - base[3]:+d}" if name != base[0] else "-"
    print(f"{name:16} {polys:8} {dp:>9} {tiles:6} {isl:8} {di:>9} {share:7.1f}%")

if reference:
    n, share = reference
    print(f"\nreference (live store): {n} islands, largest {share:.1f}% of walkable area")
    print(f"baseline build differs from it by {base[3] - n:+d} islands "
          f"({(base[3] / n - 1) * 100:+.1f}%) — the two builds should agree, so a large gap here "
          "means the sweep is not reproducing the shipped mesh and the numbers below are suspect.")
