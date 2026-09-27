# Contributing to WoW Farming Rig

Thanks for helping improve the project. Bug reports, reproducible client findings, documentation fixes, profiles, routes, and code changes are welcome.

## Before opening an issue

Search the [current issue tracker](https://github.com/DimPap99/WoW-Farming-Rig/issues) and check the [troubleshooting guide](docs/troubleshooting.md). For a bug, include the WoW client version, add-on version, capture backend, steps to reproduce, expected and actual behavior, and relevant logs. Use the repository's issue templates when possible. Please remove account and character details from logs or screenshots you share.

## Pull requests

1. Create a branch from `main` and keep the change focused.
2. Explain the behavior changed, the clients affected, and how you verified it.
3. Update the relevant guide in [docs/](docs/index.md) when behavior, configuration, or requirements change. Keep the README's support table and quick start accurate.
4. For .NET changes, initialize the submodule with `git submodule update --init --recursive`, then run `dotnet build WoWFarmingRig.sln` and any relevant tests. For add-on changes, report the client versions tested.

The solution file is `WoWFarmingRig.sln`; build it from the repository root. The project name in user-facing text is **WoW Farming Rig**.

The [architecture guide](docs/architecture.md) describes the components; [Forever port status](docs/forever-port.md) records what is currently diagnostic and what still blocks automation.

Maintainers can follow [Keeping the fork current](docs/upstream-sync.md) to merge fixes from the upstream `dev` branch into this project's `main` branch.
