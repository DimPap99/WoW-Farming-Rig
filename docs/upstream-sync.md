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

If Git reports conflicts, resolve them and complete the merge before building or pushing.

Upstream keeps the Leaflet map in one `Frontend/wwwroot/script/leaflet-watch.js`; here it
is split into pieces loaded in order, so an upstream change to that file conflicts with
the split. While the merge is in progress, let the script merge it as one file and split
it again, then mark it resolved:

```powershell
node scripts/leaflet-pieces.mjs merge upstream/dev
git add Frontend/wwwroot/script/leaflet-*.js
```

It reads our side from `HEAD`, so the conflicted working copy does not matter. If both
sides changed the same lines it writes `leaflet-watch.merged.js` with conflict markers
instead; resolve them there and run `node scripts/leaflet-pieces.mjs split leaflet-watch.merged.js`. Check changes to client detection, add-on data, and pathfinding against the [Forever port status](forever-port.md), since those areas have fork-specific behavior.
