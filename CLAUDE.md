# WoW Farming Rig - Claude Code Guidelines

## bash commands
* don't pipe to /dev/nul. we run git bash on windows and it doesn't work.
* in case the file exists use `rm -f "./WoW-Farming-Rig/nul"`

## Project Overview
Multi-project .NET 10 solution (WoWFarmingRig.sln) with Blazor Server frontend, SignalR communication, and various utility projects.
It is moving to separate services (Pathing Service, Bot Manager, Control Center) plus one resident bot
agent per game PC - read `docs/service-architecture.md` before changing how the parts talk to each other.
Problems spotted but not fixed on the spot go in `docs/known-issues.md` - check it before working on
pathing, the hosts or the authoring endpoints, and add to it.

## Language & Framework
- **Target:** .NET 10 (`net10.0`) with C# 14 (`LangVersion: preview`)
- **SDK:** 10.0.100 (defined in `global.json`)
- Use latest C# 14 features: primary constructors, collection expressions, `field` keyword, extension types, etc.
- Nullable reference types are enabled project-wide

## Build & Test Commands
```bash
dotnet build WoWFarmingRig.sln
dotnet test                                   # Microsoft.Testing.Platform (global.json); runs ServiceTests
dotnet test --project ServiceTests            # just the service tests
dotnet run --project BlazorServer
PathingService/run.sh                         # the Pathing Service on :5010, Swagger at /swagger
dotnet run --project Benchmarks -c Release
```

## Constants
  - Replace magic strings/numbers with `public const` fields for cross-file discoverability
  - Place constants in the owning class to enable Find All References and compile-time safety

## User facing API changes
- When `Core\Requirement\RequirementFactory.cs` changes a user-facing requirement, update `docs/requirements.md` and the README if the feature summary changes.

## Code Style
Existing `.editorconfig` defines style rules. Key conventions:
- File-scoped namespaces
- Explicit types preferred over `var`
- Expression-bodied properties/indexers/accessors, but not methods/constructors
- Use pattern matching and null propagation
- Prefer braces for control statements

## Project Structure
Directories marked ✎ have their own `CLAUDE.md` with the gotchas that live there - read it
before working in one.

- `BlazorServer/` ✎ - Main entry point: bot + web UI on :5000
- `HeadlessServer/` ✎ - Same bot, CLI only; benchmarks chdir here
- `Core/` ✎ - Core business logic (GOAP, goals, requirements, input). WoW: Forever code
  lives mostly in `Core/Forever`; see "Code layout" in `docs/forever-port.md`
- `Game/` ✎ - WoW process, input (stateful - read before touching), screen contracts
- `Frontend/` ✎ - Blazor UI shared by BlazorServer and PathingAPI
- `PPather/` ✎ - Pathfinding (DotRecast navmesh + legacy spot A*, MPQ reading)
- `PathingAPI/` ✎ - Standalone pathing server, bake host, Leaflet map (legacy; its pages move to the
  Control Center and its API to PathingService)
- `PathingService/` ✎ - The Pathing Service: concurrent route planning and the authoring API (paths,
  cost zones); plain `net10.0`, never references Core/Game/WinAPI/Frontend
- `Contracts/` - Wire types and route constants shared by every service and client; no dependencies
- `ServiceClients/` - Typed clients for the services (`IPathingClient`), shared by UI and agent
- `ServiceTests/` - xUnit tests for the services, the planner and the stores; runs on Linux
- `DataConfig/` ✎ - Every on-disk path; era vs client partitioning
- `Benchmarks/` ✎ - BenchmarkDotNet + the `--bake-profile` byte-identity gate
- `CoreTests/` ✎ - Integration test suites
- `Utilities/` ✎ - Offline data tools (BakeTool, ReadDBC_CSV, ...) - **not in the .sln**
- `scripts/` ✎ - Data generation and CDN upload/download
- `Addons/DataToColor/` ✎ - The in-game Lua addon
- `SharedLib/` ✎ - Shared bottom layer (ClientVersion, startup options, DBC models)
- `WowheadDB/` ✎ - Zone data model (Area/NPC/Node); field names are the wire format
- `WinAPI/` ✎ - Win32 interop; the only Windows-only dependency, keep it isolated

## Dependencies
Central package management via `Directory.Packages.props`:
- **Logging:** Serilog with structured logging
- **Serialization:** Newtonsoft.Json, MessagePack, MemoryPack
- **UI:** Blazor Bootstrap, MatBlazor
- **Benchmarking:** BenchmarkDotNet

## Architecture Patterns
- Constructor-based dependency injection via `Microsoft.Extensions.DependencyInjection`
- SignalR for real-time client communication (MessagePack protocol)
- Async/await throughout - avoid blocking calls

## Testing
- Service code is tested with xUnit v3 in `ServiceTests` (`dotnet test`); tests that need the baked navmesh
  skip themselves when the tiles are not downloaded
- Run benchmarks in Release mode: `dotnet run --project Benchmarks -c Release`
- Use BenchmarkDotNet `[Benchmark]` attributes for performance testing

## Git Workflow
- Main branch: `main`
- Create feature branches from `main`
