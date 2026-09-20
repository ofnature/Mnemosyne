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
`service.log`, and the exe marker the plugin reads.

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

## Requirements

- Windows, and a .NET 10 runtime for the service and tools.
- For building meshes: the game's installed data files.
- For queries: a cached mesh is enough — the game need not be running.

## Status

Early development, and used daily for that: the query engines and the pipe are exercised headlessly,
while the newer work (off-mesh links at doorways, smoother flight, mesh building for whole zones) is
younger than the rest. Design notes are kept locally rather than in this repo.
