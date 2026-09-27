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
- **Inspect**: `components`, `reachmap`, `reachcells`, `probe`, `flyprobe`, `layout`, `territories`,
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
Mnemosyne.Cli linktest <zone-substring> [--max-gap=2] [--max-drop=0.5] [--top=12] [--out=<dir>]
```

It loads an existing mesh, finds pairs of large islands separated by a small gap at nearly the same
height — a seam the rasterizer split, not a place the agent has to climb — links them both ways at mesh
level with `LinkPoints`, saves the result into `scratch/`, and reports whether a path between the two
points goes from unroutable to routable. On `ffxiv_roc_r1_fld_r1f1_level_r1f1`:

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
