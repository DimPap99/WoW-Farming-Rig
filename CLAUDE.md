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
dotnet test                                   # Microsoft.Testing.Platform (global.json); ServiceTests and AgentTests
dotnet test --project ServiceTests            # just the service tests
dotnet test --project AgentTests              # the tests that need Core (Windows TFM; runs on Linux too)
dotnet run --project BlazorServer
PathingService/run.sh                         # the Pathing Service on :5010, Swagger at /swagger
ControlCenter/run.sh                          # the Control Center on :5020 (start the Pathing Service first)
BotManager/run.sh                             # the Bot Manager on :5030, Swagger at /swagger
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
- `HeadlessServer/` ✎ - Same bot, CLI only, on the Generic Host; with `--agent` the PC's resident bot agent
  (Classic or Forever runs the Bot Manager assigns), kept running by `run-agent.ps1` and a logon task
  (`install-agent-task.ps1`); benchmarks chdir here
- `BotAgent/` ✎ - The agent's machinery without the bot: identity, the link to the Bot Manager, the run
  host (stop releases input first), the data sync from the Pathing Service, telemetry; no Core, tested
  on Linux in ServiceTests
- `Core/` ✎ - Core business logic (GOAP, goals, requirements, input). WoW: Forever code
  lives mostly in `Core/Forever`; see "Code layout" in `docs/forever-port.md`
- `Game/` ✎ - WoW process, input (stateful - read before touching), screen contracts
- `Frontend/` ✎ - BlazorServer's pages and components (the bot's dashboard, its map page)
- `MapAssets/` ✎ - The Leaflet map's scripts, libraries and styles, static files only; served by
  BlazorServer
- `ControlCenter/` ✎ - The central web UI on :5020: the World workspace - every world page as a mode
  over one deck.gl map (explore, the route designer, NPC routes, cost zones, navmesh and bakes, live
  routes) -, Forever route files as text, and the fleet with a console per bot; a client of the Pathing
  Service and the Bot Manager, references Contracts, ServiceClients and ServiceHosting only
- `PPather/` ✎ - Pathfinding (DotRecast navmesh + legacy spot A*, MPQ reading)
- `PathingService/` ✎ - The Pathing Service: concurrent route planning, the authoring API (paths, cost
  zones), world data, bakes and the watch hub; plain `net10.0`, never references Core/Game/WinAPI/Frontend
- `BotManager/` ✎ - The Bot Manager: the registry of bots, assignments, commands (start, stop), run history,
  the hub agents dial out to and the fleet stream for the Control Center; references Contracts,
  ServiceClients and ServiceHosting only
- `Contracts/` - Wire types and route constants shared by every service and client; no dependencies
- `ServiceHosting/` ✎ - What the ASP.NET hosts share: the key's guard and the UIs' host check, the forwarders'
  guard, the services' logging, `ApiResult` and its HTTP answers, a hub's send loop; never referenced by the
  agent or the bot
- `ServiceClients/` - Typed clients for the services (`IPathingClient`, `IBotManagerClient`), shared by UI and
  agent, how a client dials a service's hub and stays on it (`ServiceHubs`), the key rules every service
  applies (`Security/ServiceKeyPolicy`), the one rule for a library file's name and listing (`LibraryName`),
  assignment rules, the path
  editing model (`PathEditor`), map-world arithmetic (`ZoneMath`) and the route design model (`Routes/`:
  `RouteEditor` with node and leg kinds and undo, `RouteLegs` planning through any planner, what a bot
  follows of a design, the Forever route writer and reader)
- `ServiceTests/` - xUnit tests for the services, the planner and the stores; runs on Linux
- `AgentTests/` - xUnit tests that need Core (Forever's run, the agent's runtimes); the Windows TFM,
  but nothing in it attaches to a game, so it runs on Linux too
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
  skip themselves when the tiles are not downloaded. A test that needs Core goes in `AgentTests`
- Run benchmarks in Release mode: `dotnet run --project Benchmarks -c Release`
- Use BenchmarkDotNet `[Benchmark]` attributes for performance testing

## Git Workflow
- Main branch: `main`
- Create feature branches from `main`
