# Project overview

## Components

The system is composed of two main layers that communicate through pixel color encoding — no memory tampering or DLL injection.

- **Lua Addon** (`Addons/DataToColor/`) — Runs inside the game client. Reads game state (player, target, buffs, equipment, action bars, etc.) and encodes it into pixel colors on hidden UI frames, with event-driven caching for performance.
- **C# Backend** (13 .NET 10 projects) — Reads the encoded pixels via screen capture, decodes game state, makes decisions, and sends input back to the game via simulated keyboard/mouse messages. Runs as a web application ([BlazorServer](../BlazorServer/)) or a CLI tool ([HeadlessServer](../HeadlessServer/)), with a central [Pathing Service](../PathingService/) and [Control Center](../ControlCenter/) to watch and author routes from any machine. See [`docs/architecture.md`](architecture.md) for the full project breakdown.
- **DotRecast fork** (`external/DotRecast`, git submodule) — the [pinned DotRecast branch](https://github.com/DimPap99/DotRecast/tree/wow-mods), which the navigation system uses. The fork originally made on-demand baking of a 34,000-yard world practical on a player's machine. Normal navigation now uses downloaded, pre-baked tiles; local baking remains available for custom settings. Clone with `--recurse-submodules`.

The fork carries two kinds of change: `RcVec3f` is aliased to `System.Numerics.Vector3`
so geometry crosses the boundary without conversion, and a measured bake-performance
campaign narrowed the hot data structures (`areas` to `byte[]`, `RcCompactSpan` packed
16→8 bytes like the C++ original). Every commit is gated on the baked output staying
**byte-identical** — a per-tile SHA-256 over a 6-tile corpus — so none of it can change
where the bot walks.

📖 **[DotRecast fork rationale](dotrecast-fork.md)**
— the write-up of what was changed and why, with the numbers. Worth reading if you use
DotRecast yourself: the findings are about DotRecast, not about this bot, and several
apply to any C# Recast workload. It covers where the C# port pays a penalty against the
C++ original (the memory-bound sweeps, ranked by how much wider the storage is), the
benchmarking protocol that made the numbers trustworthy — interleaved A/B on an idle
box, because run-to-run spread on a dev machine is ±10–15% and a single before/after
pair proves nothing — and the two optimisations that looked obvious, were measured, and
were **reverted for being slower**.

The same material, commit by commit, is in
[`docs/dotrecast-fork.md`](dotrecast-fork.md).

## Architecture

The bot is a Lua addon that encodes game state into pixel colors, and a .NET solution
that reads those pixels, decides what to do, and sends simulated input back. Thirteen
projects, layered so each host composes only what it needs.

**Full details — project layout, dependency flow, technology stack and design
decisions — are in [`docs/architecture.md`](architecture.md).**

## Technical Challenges

The game automation domain spans a lot of engineering ground: real-time screen capture,
GPU compute, native interop, pathfinding, and a small expression language for class
profiles. Each problem and how it was solved is written up separately.

**See [`docs/technical-challenges.md`](technical-challenges.md).**
