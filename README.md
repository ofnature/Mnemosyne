# Mnemosyne

**Navmeshes that outlive the game session.**

Mnemosyne is an out-of-process navmesh build and query service for Final Fantasy XIV. Meshes live
outside the game process, so a zone's mesh survives a reload, one build serves several game
clients, and a query can be answered without the game running at all.

It is the sibling of [Ariadne](https://github.com/ofnature/Ariadne), the Dalamud plugin that
bridges the live game session to this service.

## What it does

- **Serves paths**: walk and fly pathfinding over a zone's mesh, with the classified answers
  consumers act on (`meshNotReady`, `startOffMesh`, `targetOffMesh`, `noRouteOnMesh`, `unreachable`).
- **Answers reachability**: which walkable ground is reachable from a point, as a world-aligned grid
  of stacked surfaces — for exploration tooling rather than one path at a time.
- **Talks to the game's plugins**: a named pipe (`\\.\pipe\mnemosyne`) carrying JSON requests. The
  wire format is specified in the Ariadne repo:
  [docs/mnemosyne-protocol.md](https://github.com/ofnature/Ariadne/blob/main/docs/mnemosyne-protocol.md).
- **Builds meshes**: from the game's own data, into its own store, with a CLI that can also inspect
  and sweep what is already cached.
- **Records what happens in play**: traversal reports from the game feed the off-mesh-link evidence
  (a doorway a player walked through that the mesh says is closed is still a path).

## One service per machine

The pipe is single-instance. The service holds a `Global\MnemosyneService` mutex so that four game
clients racing to autostart it cannot end up as two servers on one pipe; a second launch simply
exits. Two consequences worth knowing:

- "I rebuilt and nothing changed" is the expected failure mode after editing service code. Use
  `run-service.ps1 -Restart`.
- Every build goes to its own folder under `bin\serve`, never over the running service. A running
  service locks its own DLLs, so building in place fails — and stopping it first does not help,
  because Ariadne relaunches it within half a second of the pipe breaking, usually mid-build from
  the old files. So the script builds beside the running service, points the marker file Ariadne
  reads at the new exe, and only then stops the old one.

State lives under `%APPDATA%\Mnemosyne`: built meshes in `built\`, the service's own log in
`service.log`, and the exe marker the plugin reads. Copies that Ariadne staged from its own package
live in `service\<version>\` — and they never displace a hand-built service, because the plugin
resolves a configured path first, then the marker, then its bundled payload.

## Projects

| Project | Role |
|---|---|
| `Mnemosyne.Protocol` | The wire format: request/response shapes and the pipe name |
| `Mnemosyne.Client` | The named-pipe client the tools here share |
| `Mnemosyne.Core` | The query engines: pathfinding, reachability, geometry, overrides |
| `Mnemosyne.Service` | The server: zone lifecycle, op dispatch, one loaded set of zones |
| `Mnemosyne.Builder` | Mesh building from game data |
| `Mnemosyne.Cli` | The headless toolbox and the test harness (see below) |
| `Mnemosyne.Viewer` | A standalone viewer: navmesh and volume geometry, path tools, zone picker |

## The CLI is the test harness

There is no unit-test project here by design: the toolbox runs headless against the real mesh cache
(`%APPDATA%\XIVLauncher\pluginConfigs\vnavmesh\meshcache`) and this service's own built store, which
is why a change can be verified without the game running.

```
Mnemosyne.Cli <command>
```

- **Build**: `build` (a zone's mesh), `buildzone-test`
- **Inspect**: `trace` (route files, leg by leg), `transitions`, `components`, `reachmap`, `reachcells`, `probe`, `flyprobe`, `layout`, `territories`,
  `customizations`, `doors`, `solids`
- **Verify**: `conformance` (against the reference build), `ipc-test` (a round trip through the real
  pipe), `bench`, `fly-bench`, `plan-test`, `override-test`, `padcheck`, `snags`, `capture-test`
- plus the rest in `src/Mnemosyne.Cli/Program.cs`

### Varying one build's settings (the island sweep)

`build` takes flags, so a single build can be varied without touching the live store:

```
Mnemosyne.Cli build <zone-substring> [--out=<dir>] [--clear=<filter,...|all>] [--cell=<f>] [--ch=<f>] [--radius=<f>]
```

`--out` writes the result elsewhere. Without it `build` overwrites
`%APPDATA%\Mnemosyne\built\<zone>.navmesh` — the mesh the running service serves — which is what a
human rebuilding a zone wants, and what a sweep must never do.

The flags exist for this:

```bash
bash tools/island-sweep.sh <zone> [--quick]     # same zone under several filter/cell configurations
python tools/island-sweep-report.py scratch/sweep-<zone>
```

Both write into `scratch/` (gitignored) and read the live store only as a reference. The report's
island count and largest-island share answer the question that matters: is the fragmentation caused
by the walkable-area filters — in which case clearing one collapses the island count — or is the zone
genuinely made of many small walkable pieces, in which case the fix is connectivity (off-mesh links,
region merge) and not filter tuning. Every build prints the settings it actually used
(`filters = ...`, `cell = ...`), so "identical to the baseline" is never ambiguous. The three
territories that hand-clear `LedgeSpans` in their customizations are exactly the ones where tweak
ordering matters, which is why the tweak is applied after the customization has had its say.

### Testing connectivity (linktest)

Measured on the worst offender, the filters and the agent radius are both innocent: clearing filters
*adds* islands, and shrinking the radius from 0.5 m to 0.25 m raises them by 28%. The fragments are
places a 0.5 m agent genuinely cannot pass, so the remaining lever is connectivity. `linktest` tests
the narrowest useful form of it:

```
Mnemosyne.Cli linktest <zone-substring> [--anchor=x,y,z] [--limit=10] [--mesh=<file>]
                        [--max-gap=2] [--max-drop=0.5] [--top=12] [--no-collision] [--apply [--apply-to=<dir>]]
```

It loads an existing mesh, finds pairs of islands separated by a small gap at nearly the same
height — a seam the rasterizer split, not a place the agent has to climb — links them both ways at mesh
level with `LinkPoints` in memory, and reports whether a route goes from unroutable to routable.

**Use `--anchor`.** Without it the search takes the largest islands, and largest is not playable: all six
Coerthas seams in the first table below turned out to be on a flat sheet at Y 19.8 and a ledge at Y 191,
160–300 y under the ground Camp Dragonhead stands on and unreachable from it. `--anchor` (an aetheryte,
a zone entrance) keeps only seams on the edge of the ground reachable from that point, largest far side
first, and measures the route *from the anchor*. From Camp Dragonhead's aetheryte it finds ten, from
40 y to 800 y away, and all ten go from a partial route that stops one step short to a complete one.

**Every candidate is checked against collision.** The mesh cannot tell a crack from a wall between two
floors, and the first link tried in game was masonry. `linktest` casts rays across each gap at 0.6, 1.2
and 1.8 y against the zone's collision and lists what blocks as walls instead of linking them. From
Camp Dragonhead that rejected 64 of 66, mostly rocks, terrain and gates.

**`--apply` writes override links, not a mesh.** A mesh changed by `LinkPoints` does not survive
serialization — the reloaded Coerthas file lost every link and the ground around the aetheryte
(2026-09-29). vnavmesh avoids this by writing its cache *before* a customization runs and re-running it
after every load. So `--apply` appends the seams as bidirectional `Links` to the zone's override JSON,
which the service stitches into routes at query time and reloads on change; it keeps existing links,
backs up the previous file and prints the rollback. `--mesh=<file>` links an exact file, such as a live
capture in `captured\`, which the cache lookup does not search.

Earlier measurements, on `ffxiv_roc_r1_fld_r1f1_level_r1f1` without an anchor (so they prove the
mechanism, not a gameplay benefit):

| candidates | before | after | off-mesh polys added |
| --- | --- | --- | --- |
| 6 (largest 12 islands, gap ≤ 2 m) | 0/6 routable | **6/6** | 24 |
| 41 (largest 40 islands, gap ≤ 3 m) | 0/41 routable | **41/41** | 164 |

**Why mesh level rather than the create-params route** (`AddOffMeshConnection`), which was this test's
first version: that one requires both ends inside one tile — Recast builds the connection into a single
tile's poly mesh, and the extension throws otherwise — so it silently drops every candidate that
straddles a tile boundary. (Its attribution also has to use the tile's *own* box: the rasterization box
is padded by the border size and overlaps its neighbours, which turned six candidates into fifty
connections.) `LinkPoints` inserts a point-poly at each end plus an explicit tile link, so its link may
reference any tile, it needs no rebuild, and it is the same mechanism the per-territory customizations
already use for doorways and parapets. It costs two off-mesh polygons per directed link, which is why
the wide run adds 164 for 82 links.

Conservative by construction — same height (≤ 0.5 m) and short gaps (≤ 3 m) — because a link between
distant points would reroute everything, and a bad auto-applied link is worse than no link. Widening
those thresholds is a decision to take with numbers in hand, and the numbers are one `linktest` away.

### Tracing a route (trace)

```
Mnemosyne.Cli trace [route-file-or-directory] [--all]
```

This replays a dungeon route file leg by leg against the mesh the service would serve, choosing it
the way the service does: a live capture first, then vnavmesh's cache, then the offline build. It
reads Theseus route files, and by default all of `%APPDATA%\XIVLauncher\pluginConfigs\Theseus\paths`.
It also reads AutoDuty's files. For each leg that does not simply walk, it tries each layer of data
in turn and names the first one that completes it:

| verdict | meaning |
| --- | --- |
| `walk` / `DETOUR` | the mesh alone; a detour is over 3× the straight line + 10 y |
| `fixed by override link` | the zone's override links, which the service already serves |
| `covered by transition` | a listed ride, slide or lift, boarded at its trigger. The `transitions` spec's later findPath opt-in would route this |
| `covered by field evidence` | a crossing characters were seen to make (evidence `LinkCandidates`) |
| `by hand` | the route file crosses it itself: the leg starts at an `AutoMoveFor` or `Jump` step |
| `after an interaction` | the route uses an `Interactable` on the way: a lever lift, or a door it opens |
| `UNEXPLAINED` | none of the above: a mesh gap, or a crossing we have no data for |
| `OFF MESH` | a step's position is over 5 y from walkable mesh |

For an unexplained leg, the trace also says how far the best transition gets, and names a door or
arena barrier within 8 y of where it stops. A ride lands at its spawn marker (`PopRange`) when it has
one, because that is where the game hands the character over. Snapping the path's last point instead
put Xelphatol's second shuttle on a sealed 15 m² pad. Legs that walk over collision
from a `*navimesh*` layer are flagged. That collision is designer scaffolding for the game's own
navigation, and the mesh treats it as floor; The Ghimlyt Dark's last drop has a 50 × 30 y board of it.
With a directory, a table at the end has one row per route. A dungeon never entered on this machine
has no mesh and is listed as such.

## Requirements

- Windows, and a .NET 10 runtime for the service and tools — **unless you are only consuming this
  through Ariadne**. The [Ariadne](https://github.com/ofnature/Ariadne) plugin package carries a
  self-contained copy of the service and its CLI under `service/`, runtime included, which it stages
  to `%APPDATA%\Mnemosyne\service\<version>\` and launches by itself. Building from source is for
  changing the service, not for running it.
- For building meshes: the game's installed data files.
- For queries: a cached mesh is enough — the game need not be running.

## Status

Early development, and used daily for that: the query engines and the pipe are exercised headlessly,
while the newer work (off-mesh links at doorways, smoother flight, mesh building for whole zones) is
younger than the rest. Design notes are kept locally rather than in this repo.
