# Keeping the fork current

WoW Farming Rig is a fork of [Xian55/WowClassicGrindBot](https://github.com/Xian55/WowClassicGrindBot). This repository develops on `main`; the upstream repository develops on `dev`. The shared Git history lets us merge upstream fixes while retaining the Forever port and other project changes.

Set up the upstream remote once in a clone of this repository:

```powershell
git remote add upstream https://github.com/Xian55/WowClassicGrindBot.git
```

To review and merge new upstream work:

```powershell
git switch main
git pull --ff-only origin main
git fetch upstream dev
git log --oneline main..upstream/dev
git merge upstream/dev
git submodule update --init --recursive
dotnet build WoWFarmingRig.sln
git push origin main
```

If Git reports conflicts, resolve them and complete the merge before building or pushing. Check changes to client detection, add-on data, and pathfinding against the [Forever port status](forever-port.md), since those areas have fork-specific behavior.
