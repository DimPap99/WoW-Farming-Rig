# WoW: Forever port status

This is a work-in-progress port for the **Forever beta 1.60.1.70009** client
(`WowB.exe`, interface `16001`). The dashboard now detects this process and
classifies it as `forever`; the matching `DataToColor_Camelot.toc` loads in the
client. **Dashboard startup remains guarded.** The explicit controller utility
now runs bounded target acquisition, mouse pursuit, combat, looting and recovery.
`DataToColor:OnInitialize` intentionally stops before creating Classic pixel
frames or changing CVars on Forever.

## Reproduce the current setup

1. Install the .NET 10 SDK and initialize the `external/DotRecast` submodule.
2. Build with `dotnet build BlazorServer/BlazorServer.csproj -c Release`.
3. Copy `Addons/DataToColor` to the Forever beta client's
   `_classic_beta_/Interface/AddOns/DataToColor` directory. The client must
   select `DataToColor_Camelot.toc`. Restart the game after copying add-on files.
4. Copy `Addons/ForeverProbe` to the beta client's
   `_classic_beta_/Interface/AddOns/ForeverProbe` directory. It supplies the
   visual data required by the Forever dashboard and controller trial:
   `ForeverEvents.lua` latches cast, death and loot events,
   `ForeverNameplates.lua` keeps the nameplate roster and its markers,
   `ForeverVisual.lua` draws the channel, and `ForeverDiagnostics.lua` holds the
   one-shot probes. Its
   optional `/foreverprobe` command records API value types in `ForeverProbeResults`
   under the beta client's account-wide SavedVariables. The out-of-combat
   `/forevercatalog` command records only numeric spellbook/action-slot IDs and
   action type labels in `ForeverProbeCatalog` (saved on `/reload`); it does not
   record character names, item names, or macro text. Neither command controls
   gameplay.
   `/forevervisual` toggles a status-bar prototype at the upper-left. Its first
   five rows are player health (green), player power (blue), target health
   (red), 50% calibration (yellow), and 100% calibration (white). Five tiny RGB
   squares underneath carry map ID, normalized map X/Y, facing, and a validity
   marker; the marker stays black if any position source fails. Another square
   indicates target presence (green = present, red = absent, black = unknown).
   A compact six-row grid beneath the bars carries type and ID for all 120
   action slots; each slot explicitly distinguishes empty from unreadable.
   A second grid carries up to 200 spellbook slots. Each entry contains a
   checksummed numeric ID and whether it is a spell ID or a flyout action ID.
   The count has its own marker; an incomplete grid does not initialize the
   spellbook reader. A book with more than 200 slots is unavailable.
   A third grid carries the existing addon's compact key-binding codes for 62
   binding indices, including valid unbound entries. It reads the current
   bindings without changing them and checks each entry before updating the
   binding reader. This grid requires both installed addons.
   A second pixel row carries `UnitPosition` world X/Y/Z and instance map ID
   at 0.01-yard resolution. Map coordinates, world coordinates, and player
   vital maxima include a checksum marker; a damaged pixel is treated as
   unreadable. These coordinates are diagnostic data only.
   Twelve cyan bars show the remaining cooldown fraction of main-bar slots 1 to
   12, a further grid carries each of the 120 slots' usable, mana and range
   state, and the aura strip compares player aura icons with each main-bar
   slot's icon. The single watched-action row and `/forevercooldown` were
   retired on 2026-10-03; the slot grids carry the same data for every slot.

The read-only visual diagnostic can capture a lossless PNG, decode an existing
PNG, or monitor those bars for a bounded time:

```powershell
dotnet run --project Utilities/ForeverVisualReader -c Release -- --self-test
dotnet run --project Utilities/ForeverVisualReader -c Release -- --reader-smoke
dotnet run --project Utilities/ForeverVisualReader -c Release -- --route-probe
dotnet run --project Utilities/ForeverVisualReader -c Release -- --monitor 45
dotnet run --project Utilities/ForeverVisualReader -c Release -- --read path\to\capture.png
```

If multiple `WowB.exe` processes are open, add `--pid <process-id>` to the
monitor/capture command to select the intended window. The reader does not
construct `BotController` or send keyboard/mouse input.
`--route-probe` checks that the live map and world X/Y agree, samples a
walkable height from the baked local mesh, and plans one short disk-only route
from that point. It uses neither the client's zero Z nor game input.

The separate `ForeverMovementProbe` keeps its input path explicit. Run its
`--check` mode from the repository root first; this only reads the screen and
mesh. `--move` repeats the same preflight and sends one 200 ms Up Arrow press,
then releases the key and measures displacement. It requires at least 95%
health, no selected target, consistent map/world coordinates, and a short
straight baked route. It never starts the normal bot or combat controller:

```powershell
dotnet run --project Utilities/ForeverMovementProbe -c Release -- --check
dotnet run --project Utilities/ForeverMovementProbe -c Release -- --move
```

The first live `--move` test in Undercity passed its straight three-yard
preflight with zero new bakes. One 200 ms Up Arrow press moved the character
1.35 yards: 1.34 yards along facing and 0.05 yards laterally. A five-second
read-only follow-up showed no continued movement after key release. This
validates one bounded input step, not route following or autonomous combat.
A second requested press passed the same preflight and moved 2.11 yards along
facing with no measurable lateral displacement.

The Blazor dashboard now also has a **Forever diagnostic mode**. With Forever
running, start `BlazorServer` normally and open its dashboard. It selects WGC,
shows fresh player health/power and target-health percentages from the first
five bars, plus what each main-bar slot holds and its remaining cooldown.
It reports when the bars are missing or capture has stopped. A separate marker
distinguishes an absent target from a target at zero health; an unreadable
marker is reported as unknown. When the position marker is valid, the
dashboard also shows map ID, normalized map coordinates, and facing. It does not
offer class-profile loading or a Start button, and it never constructs the
pathing/GOAP/input controller. Forever startup skips the Classic add-on/frame
validation and cleanup path; running the dashboard will not delete the Forever
add-on just because classic `addon_config.json` is absent. The headless host
refuses Forever before its old flush-key input step.

The Forever beta's 1.60.1.70009 DBC-derived data lives in `Json/dbc/forever`.
Regenerate with:

```powershell
dotnet run --project Utilities/ReadDBC_CSV --configuration Release -- -v forever worldmap faction item consumable spell icon areatable
```

The resulting intermediate CSV cache in `Utilities/ReadDBC_CSV/data` is ignored
by Git; the generated JSON is not.

The separate `Utilities/ForeverCasc` tool now contains a reproducible,
read-only `IGameFileSource` backed by an MIT-licensed TACTSharp source snapshot.
It opens only the installed client's local CASC data, with CDN and listfile
downloads disabled. Supply a local wowdev-style `id;path` geometry listfile;
the listfile is not shipped with this repository. To verify one file while the
beta client is running:

```powershell
dotnet run --project Utilities/ForeverCasc -c Release -- "C:\Program Files (x86)\World of Warcraft" "C:\path\to\community-listfile.csv" 775971
dotnet run --project Utilities/ForeverCasc -c Release -- "C:\Program Files (x86)\World of Warcraft" "C:\path\to\community-listfile.csv" "World\Maps\Azeroth\Azeroth_32_48_obj0.adt"
dotnet run --project Utilities/ForeverCasc -c Release -- "C:\Program Files (x86)\World of Warcraft" "C:\path\to\community-listfile.csv" --wdt-azeroth
dotnet run --project Utilities/ForeverCasc -c Release -- "C:\Program Files (x86)\World of Warcraft" "C:\path\to\community-listfile.csv" --sample-bake-azeroth
dotnet run --project Utilities/ForeverCasc -c Release -- "C:\Program Files (x86)\World of Warcraft" "C:\path\to\community-listfile.csv" --sample-route-azeroth
dotnet run --project Utilities/ForeverCasc -c Release -- "C:\Program Files (x86)\World of Warcraft" "C:\path\to\community-listfile.csv" --sample-liquid-azeroth
```

An optional fourth argument selects the product (default `wow_classic_beta`);
an optional fifth argument selects the local cache directory. The sample bake
reads collision geometry and builds one Detour tile in memory. The sample route
checks WMO placement bounds, MCNK collision culling, and a path across two
adjacent tiles. The liquid check compares two installed ADTs with their raw
MH2O masks and bakes one wet navmesh tile. These commands do not write a tile
to the runtime cache or enable gameplay.

The normal `BakeTool` now accepts the local Forever CASC source. Its `--adt`
pair is world grid X,Y, so the file `Azeroth_32_48.adt` is selected with
`--adt 48,32`:

```powershell
dotnet run --project Utilities/BakeTool -c Release -- --exp forever --root Json --continent Azeroth --adt 48,32 --casc-install "C:\Program Files (x86)\World of Warcraft" --casc-listfile "C:\path\to\community-listfile.csv"
dotnet run --project Utilities/BakeTool -c Release -- --exp forever --root Json --continent Azeroth --area-grid --casc-install "C:\Program Files (x86)\World of Warcraft" --casc-listfile "C:\path\to\community-listfile.csv"
dotnet run --project Utilities/BakeTool -c Release -- --exp forever --root Json --continent Azeroth --casc-install "C:\Program Files (x86)\World of Warcraft" --casc-listfile "C:\path\to\community-listfile.csv"
dotnet run --project Utilities/BakeTool -c Release -- --exp forever --root Json --continent Kalimdor --casc-install "C:\Program Files (x86)\World of Warcraft" --casc-listfile "C:\path\to\community-listfile.csv"
```

The installed beta's Azeroth bake completed 737 ADTs and persisted 11,216
navmesh tiles. Kalimdor completed 989 ADTs and persisted 15,126 tiles. These
large `.dnm` tile sets are local and Git-ignored; another machine must bake
or receive them separately. The committed area grids and subzone bounds cover
447 Azeroth areas and 415 Kalimdor areas under `Json/area_grid/forever` and
`Json/subzones/forever`. A disk-only path query returned 28 points across
three resident tiles while baking zero. Another disk-only route crossed the
tile recovered by the contour fallback and its neighbor; a Kalimdor route
crossed tiles `128,128` and `129,128` with zero bakes. The strict bulk
bake now reports worker failures; a missing WMO doodad variant is logged and
uses the root's global set while retaining WMO collision.

`PathingAPI` can use the same local CASC source for on-demand bakes. Set
`exp=forever`, `ForeverCasc__Install` to the WoW install, and
`ForeverCasc__Listfile` to the local `id;path` listfile. `ForeverCasc__Product`
and `ForeverCasc__Cache` are optional. Leave Install and Listfile empty to
serve only baked tiles. A configured HTTP bake resumed one existing Azeroth
ADT, and an unconfigured HTTP host returned the disk-only route above.

## Code layout and upstream seams

The port keeps its logic in Forever-owned files so upstream merges stay
mechanical (see [upstream-sync.md](upstream-sync.md)). When fixing Forever
behaviour, change these files rather than shared upstream classes.

Forever-owned code:

- `Core/Forever/` - the guarded runtime: `ForeverGoalFactory` (the session's
  composition root), `ForeverStateMachine` (the declared states, exits, time
  bounds, release hooks and allowances both Forever goals run on),
  `ForeverSelector` and `ForeverFacts` (which activity runs on a frame),
  `Activities/` (Engage, Threat, Loot, Rest and the agenda's Task),
  `Tasks/` (`ForeverAgenda`, the `ForeverTasks` API, `ForeverUntil` and the
  task runners), `ForeverLookAround`, `ForeverCaster` (one
  checked keypress per cast),
  `ForeverRotation` (the class profile's Pull, Combat and Adhoc lists),
  `ForeverCorpses` (the bodies of a fight and where they fell), `ForeverLoot`
  (the loot round after it), `ForeverNavigator` (also `IForeverFacing`),
  `ForeverHops` (when walking hops), `ForeverDragPoint` (where a camera drag may begin),
  `ForeverTargetPolicy` (which units to fight, which attacker next),
  `ForeverPullOptions` (which pull, by cost),
  `ForeverTargetSelector`, `ForeverPointAndClick` (aim, confirm, click,
  confirm), `ForeverExclusions` (units set aside for a while),
  `ForeverRecovery` (what a setback with one unit costs),
  `ForeverReaderFeed` (feeds each capture tick to the shared readers),
  `ForeverFrame` (which inputs a frame must carry and the resource levels),
  `ForeverBinding(s)` (the bindings checked and pressed), `ForeverNavmesh`,
  `ForeverVerifiedSetup` (the verified profiles, map and classes) and
  `ForeverProfileRules` (what a Forever profile may use).
- `Core/Goals/ForeverAutonomousGoal.cs`, `ForeverTrialGoal.cs`,
  `Core/GoalsComponent/ForeverCastGate.cs`, `Core/GOAP/ForeverGoapWorldState.cs`,
  `Core/ForeverDiagnosticController.cs`, `Core/Session/NoGrindSessionHandler.cs`.
- `Core/WoWScreen/ForeverVisualBarTracker.cs`, the `Visual*Reader` decoders and
  `VisualMarkerLocator`.
- `Game/Input/MouseCameraInput.cs`, `Game/Input/MousePointerInput.cs`,
  `Addons/ForeverProbe`, `Utilities/Forever*`.
- `Frontend/ForeverRunHost.cs` (starts and stops the trial runner),
  `ForeverRunFiles.cs` (the repository, the runner's build, routes and logs),
  `ForeverRouteCatalog.cs` (the route files a run can start from),
  `ForeverRunOutput.cs` (what a run printed, read through `ForeverRunProtocol`),
  `ForeverPages.cs` (the pages offered on Forever), `Pages/ForeverRuns.razor`
  and `Pages/ForeverStatus.razor` (the home page card).

Where Forever still touches upstream code:

- Client detection: `ClientVersion.Forever`, `StartupClientVersion` (1.60+),
  the `WowB` process name and the `forever` data era.
- Capture: `IWowScreen.ScreenFrameId`.
- Composition: `DependencyInjection` (tracker, feed, the diagnostic dashboard
  controller, skipped Classic add-on validation), a three-line hook in
  `GoalFactory` and an optional `ForeverReaderFeed` in `AddonReader`.
- Controller loop: `IsForeverVisual` guards in `BotController` and `GoapAgent`
  around Classic-only threads, validation, session handling and world state.
- Readers on the profile-load path: `PlayerReader` identity, vitals and
  position from the visual sample (every Classic cell read throws on Forever,
  because there is no pixel strip), `SpellBookReader`/`KeyBindingsReader`
  `UpdateForever`, `KeyReader` live-binding resolution, and the
  `RequirementFactory` checks that refuse unsupported profile expressions and
  skip the Classic usable/current-action requirements.
- PPather: the `IGameFileSource`/`ILiquidMetadataSource` CASC seam, the numeric
  WMO and MH2O formats, and the opt-in `NavmeshQueryOptions.PreferGroundUnderPoint`.
- Frontend: `Index.razor` shows `ForeverStatus` on Forever, `MainLayout` takes
  its navigation and page gate from `ForeverPages`, and `Screenshot.razor` hides
  `BotHeader`; `BlazorServer/Program.cs` registers `ForeverRunHost` on Forever.

A Forever-owned controller loop would let `BotController`, `GoapAgent`,
`PlayerReader`, `KeyReader` and `RequirementFactory` return to upstream as well.

### Class-agnostic rotation (2026-10-03)

The runtime names no class or spell. As upstream, the class profile carries
everything class-specific and the behaviour is shared:

- `ForeverAutonomousGoal` runs the same activities for every class: finding,
  clicking and fighting a unit, looting and resting (since 2026-10-04 as
  activities under a selector; see "Activities and the agenda").
- `ForeverRotation` reads the profile. **Adhoc** holds self heals and buffs,
  gated by the upstream `InCombat` flag. A buff is an action with
  `AfterCastAuraExpected`, and its aura is matched against its slot icon
  (`VisualBarSample.SlotAuras`). **Pull** holds openers, each used once per
  target and only while every buff is up. **Combat** is the priority list, and
  `AutoAttack` presses `STARTATTACK` whenever the attack state asks for it (see
  "Melee reach and attack state" below). Pursuit stops when any opener can be
  pressed or any Combat spell's slot reports the target in range.
- Each action's own requirements say when to use it. They are evaluated by
  `ForeverRotation` itself, because `KeyAction.CanRun` caches on the Classic
  `GlobalTime` cell, which never advances on Forever. Only `Health%`, `Mana%`,
  `TargetHealth%` and `Spell:` have a Forever source;
  `ForeverProfileRules.FindUnsupportedProfile` refuses anything else at load
  (see [requirements](requirements.md#wow-forever-requirements)).
- `ForeverCastGate` decides only whether a press is safe: the spell is ready on
  its slot and bound, the setup is verified, and a targeted action has a living,
  attackable, non-player target in range. Opening a fight out of combat needs
  70% health and, for mana users only, 60% mana; rage and energy are not gated.
- Every action is its key pressed. Upstream types a profile `MacroText` as a
  command; since 2026-10-04 Forever refuses it, since that is typed into chat.
  A self heal is its key, landing on the player through the client's Auto
  Self Cast (`/foreverprobe` records `autoSelfCast`).
- **Next-swing abilities.** An ability that resolves on the next melee swing
  (upstream `AfterCastWaitSwing`, e.g. Raptor Strike) gets its success event
  with the swing. The beta gives such an ability no usable range of its own:
  Raptor Strike's slot reads in range out to about 100 yards, and the Attack
  ability's slot (6603) reads no range. Its range therefore counts only while
  the unit is close (below). A press is proved once the slot shows it pending;
  a press the game did not take is tried again after 1.5 seconds, and neither
  ends the run.
- **Too close to open.** Out of combat, a candidate that is inside a Combat
  spell's range but outside every opener's range is passed over, judged from
  its nameplate's ranges before it is clicked; aggro still brings a close mob
  into the fight.

`Paladin_Forever_Auto.json` encodes the thresholds the Paladin code used to
hard-wire: Holy Light below 70% in combat and below 90% out of combat, Seal at
70% health and 25% mana, Judgement as the opener, then melee and Holy Strike.
`Hunter_Forever_Auto.json` (written at level 6 without a pet; the pet joined
on 2026-10-04, below) uses the same shared behaviour:
Serpent Sting (refreshed at most every 15 seconds through the upstream
`Cooldown` field) and Auto Shot at range, then melee and Raptor Strike.

`ForeverVerifiedSetup` lists what has been verified live: Tirisfal Glades on
Azeroth and Durotar on Kalimdor (`IsVerifiedMap` pairs each UI map with the
instance map its world positions report), and the Paladin and Hunter auto
profiles (`AutoProfileFor`). The controller trial loads the profile verified for
the logged-in class. A new class or map needs a profile, a live bounded sweep,
and an entry here. Rogue finishers, DoTs, forms and pet care (Mend Pet needs
the pet's health) still need channel work first; the profile rules refuse them.

Live results on 2026-10-03, level 6 Troll Hunter in Durotar: one `--auto-run`
cycle and one `--sweep` each completed with a verified kill and status
`Complete`. Serpent Sting opened from range (mana 100% to 90%), the mob came
into contact, and Raptor Strike's success event (2973) arrived with the melee
swing. The game reported both corpses not lootable, so the loot path was not
exercised on this character. Three other attempts stopped fail-closed on
"Raptor Strike success was not confirmed": twice a passive mob next to the
character never came into swing reach, and once the target was a Makrura in
the water below a shipwreck deck the character stood on. In that last run Start
Attack had turned melee auto attack on; there was simply nothing to swing at. A
reliable Hunter melee phase needed a real melee-distance signal; "Melee reach
and attack state" below found one without Wing Clip.

## Live findings from the beta client

- `WOW_PROJECT_ID` equals `WOW_PROJECT_MAINLINE` (`1`), not Classic. The
  client uses modern `C_Spell` and `C_UnitAuras` APIs, while globals such as
  `GetSpellInfo`, `UnitAura`, and `GetNumSpellTabs` are absent.
- `UnitHealth("player")`, `UnitPower("player")`, and the corresponding
  percentage APIs return **secret values even outside combat**. The old pixel
  protocol performs arithmetic on these values and therefore cannot transmit
  them. `UnitHealthMax("player")` was an ordinary number in the test.
- `C_UnitAuras.GetAuraDataByIndex("player", 1, "HELPFUL")` returned a table
  with readable spell ID/name/expiration outside combat, but errored in a
  delayed snapshot where `InCombatLockdown()` was true. The old aura reader
  cannot be enabled in combat.
- `C_Spell.GetSpellInfo` and `C_Spell.GetSpellCooldown` returned tables;
  `GetSpellCooldown`'s `startTime` and `duration` fields became secret under
  combat lockdown. `GetActionCooldown(1)` returned a secret start time. The
  old numeric cooldown encoder therefore cannot run in combat. A follow-up
  delayed combat-lockdown probe found that
  `C_Spell.GetSpellCooldownDuration(6603, true)` returned an opaque duration
  object whose `GetRemainingDuration()` was secret, while both a hidden
  `StatusBar:SetTimerDuration()` and
  `Cooldown:SetCooldownFromDurationObject()` accepted that object without a
  Lua error. That established the API display path; the later visible test
  below confirmed a changing fill for one active action cooldown.
  `UnitPosition` and `C_Map.GetBestMapForUnit` returned ordinary numbers.
- Modern `C_SpellBook` line/item/name/texture queries returned ordinary types
  both outside combat and under lockdown. The Forever-only compatibility
  adapters for the missing global spellbook functions now load, and a live
  probe saw `GetNumSpellTabs()` return a number. Action type/usability and bag
  item ID/count also stayed ordinary in that snapshot. These are API probes,
  not end-to-end addon-reader tests.
- An out-of-combat `/forevercatalog` snapshot on build 70009 recorded 17
  spellbook entries across four lines and nine occupied action slots, with no
  unreadable entries. Slot 4 was a spell action with ID 7744, matching a
  spellbook entry. Two spellbook entries were flyouts with action IDs but no
  `spellID`, so the two fields must not be treated as interchangeable. The
  Forever-only legacy spellbook shim now falls back to `actionID` for these
  entries; the guarded runtime still does not consume that shim for automation.
- `C_Map.GetPlayerMapPosition`'s X/Y, `UnitPosition` components, and facing
  remained ordinary numbers under combat lockdown. A separate visual position
  strip was decoded from live WGC capture: map ID 1420, and normalized X/Y
  changed continuously while the user walked manually (about 0.5709/0.5563
  to 0.5629/0.5432). Facing differed between captures. Synthetic tests reject
  a missing position marker at three UI scales. `PlayerReader` exposes this
  through `TryGetForeverPosition`, which returns false when the marker or
  whole visual sample is missing/stale. `PlayerReader` now supplies its
  `MapX`, `MapY`, `Direction`, and `WorldPos` X/Y from that fresh sample on
  Forever. Its visual update sets the UI map and area without reading Classic
  cells; a stale position clears the cached UI map and causes position getters
  to throw. The live reader smoke check compared the map-derived world X/Y
  with `UnitPosition`, then verified stale data was rejected. `WorldPosZ` is
  still a navigation estimate; the client's reported Z=0 is not used. This is
  **not** a navigation system or permission to remove the startup guard.
- A hidden native `StatusBar` accepted direct `SetMinMaxValues`/`SetValue`
  calls for secret player health, player power, and target health, including in
  a delayed combat-lockdown snapshot. The separate visible vitals prototype
  below validates screen capture of those fills during manual combat.
- The visible bar prototype **was** decoded from a live 1920x1080 WGC capture.
  During one manual fight the readout tracked target health from 100% to 0%,
  player health from 100% down to 77% and back up, and changing power. Its
  yellow calibration stayed at 50% across the observed frames. Synthetic
  self-tests pass at 75%, 100%, and 125% simulated UI scale. This validates a
  percentage *prototype* on this machine; more display configurations need
  testing.
- The decoder now lives in `Core/WoWScreen/VisualBarReader.cs`. The regular
  `AddonReader` update path refreshes a Forever-only tracker from each new
  screen frame, even without the old pixel strip. It rejects missing/invalid
  calibration and samples older than 750 ms. `PlayerReader` exposes the sample
  through `TryGetForeverVisualVitals`. `HealthPercent`, `PTPercentage`, and
  `TargetHealthPercent` now consume the fresh visual sample on Forever.
  `TargetHealthPercent` requires confirmed target presence, while missing or
  stale samples throw rather than falling back to legacy pixel cells. Absolute
  current health/power getters remain unsupported and explicitly reject Forever.
  The Core-backed live monitor tracked changing bars during another fight and
  returned no sample after `/forevervisual` hid them.
- Two additional visual pixels now carry player maximum health and maximum
  power only when those API results are ordinary integers; separate markers
  reject unreadable values. Synthetic checks pass at 75%, 100%, and 125% UI
  scale. After a beta `/reload`, live WGC capture decoded health max 272 and
  power max 230. `PlayerReader.HealthMax()` and `PTMax()` returned those values
  and rejected a stale sample. In a later manual fight, both maxima stayed
  readable while player health fell from 95% to 80%. Current health and power
  are still secret and are not inferred from the bar fill as exact integers.
- A live world-position read in Tirisfal returned `(1934.80, 2441.60, 0.00)`
  on instance map 0 while map `1420` showed normalized `(0.13094, 0.63160)`.
  The committed map transform predicts `(1934.80, 2441.65)` in world X/Y,
  within 0.05 yards. A second position returned `(1909.80, 2476.70, 0.00)`;
  its map transform predicts `(1909.83, 2476.71)`. Both positions land on
  baked Azeroth tile `146,142`. The mesh reported walkable heights around
  129.39 and 130.68, while `UnitPosition` reported Z=0 at both. That Z result
  is not suitable for navigation until its meaning is resolved. The visual
  sample exposes fresh world values (`VisualBarSample.WorldPosition`) for
  diagnostics, without feeding Z into the movement controller.
- The read-only live route probe in Tirisfal found map/world disagreement of
  only 0.03 yards at `(2286.30, 366.20)`, sampled mesh height 34.42, and
  planned a complete six-point route to a 12-yard local target. It used one
  resident baked tile and zero new bakes. A second moving read at
  `(2085.10, 141.20)` passed the same check across two resident tiles, also
  with zero new bakes. This validates two live placements
  and path query, not a movement trial or the full region's collision quality.
- A later low-health capture showed color noise in the numeric pixel row:
  an expected maximum of 232 appeared as 262,376 in one frame. The map and
  world-position markers were also intermittently unreadable. The numeric
  channels now checksum their payloads, so corrupted coordinates and maxima
  are rejected rather than accepted as valid large values. Synthetic tests
  cover red-channel corruption, including a uniform tint of value and marker,
  at three UI scales. After reloading the updated
  probe, a live `--reader-smoke` read both maxima and world position, then
  rejected stale world position. A five-second moving capture kept the
  checksummed map, world X/Y, and maxima readable; reported world Z stayed 0.
- The target-presence marker changed from absent to present when a target was
  selected and back to absent after it was cleared, including during a manual
  fight where target health fell to 73%. A live `--reader-smoke` check read
  player health/power as percentages and confirmed that `HealthCurrent()`
  rejects unsupported absolute values and `HealthPercent()` rejects a sample
  after it expires.
- The cyan timer bar was captured during a manual cast from action-bar slot 4.
  Its decoded fill moved from 0% before the cast to 100%, then fell through
  97%, 95%, and down to 73% during the monitor window. This demonstrates a
  visual remaining-cooldown fraction on this client; it does not establish
  precise seconds, readiness for every spell type, or automated use. The
  read-only dashboard displays the fraction only when a cyan validity marker
  is present.
- The watched action's kind, slot, and ID now have a separate read-only visual
  channel. Its decoder rejects missing or inconsistent markers at 75%, 100%,
  and 125% synthetic UI scales. A live WGC capture after a full beta relaunch
  decoded slot 4 as spell ID 7744, matching the catalog's slot and spellbook
  entries. The tracker exposes that result only while the visual sample is
  fresh; the live reader smoke check confirmed it stops returning an action
  after capture expires. This first channel covers
  only the selected slot; it is not a gameplay-ready spell reader.
- A 120-slot read-only action-bar grid and decoder are now implemented. Its
  synthetic tests verify the complete inventory, empty slots, flyouts,
  unreadable entries, and missing markers at three UI scales. The diagnostic
  dashboard shows occupied/readable counts, and `PlayerReader` only exposes the
  grid while the capture is fresh. A live WGC capture decoded all 120 slots as
  readable and nine occupied: slots 1-4, 11, 26-27, and 38-39. Their kinds
  and IDs matched the earlier `/forevercatalog` SavedVariables snapshot exactly.
  A live `PlayerReader` smoke check confirmed that the grid is rejected after
  the visual sample expires. This is metadata only: usability, range, and
  cooldowns for every slot are still not ported.
- A checksummed visual spellbook grid now feeds `SpellBookReader` on Forever.
  The decoder covers up to 200 slots and rejects missing or damaged count and
  entry markers. Synthetic checks at 75%, 100%, and 125% UI scale include the
  final row, a corrupt entry, and a flyout action ID that must not be treated
  as a learned spell. A live beta capture after `/reload` decoded 29/29 slots:
  26 spell IDs and three flyout action IDs. The live `--reader-smoke` check
  populated `SpellBookReader` from that snapshot and confirmed that both the
  visual sample and reader state are cleared after capture expiry. This is
  learned-spell metadata; spell usability, range, and complete cooldown data
  are still missing.
- The guarded Forever visual probe now reads the 62 indexed binding codes
  through `DataToColor:GetBindingEncodedByIndex`, which only queries current
  game bindings. A checksum distinguishes each unbound entry from a damaged
  pixel, and `KeyBindingsReader` resets on an incomplete or stale snapshot.
  Synthetic decoder and adapter checks passed at 75%, 100%, and 125% UI scale.
  Live capture decoded 62/62 indices with 26 bound commands. `MOVEFORWARD`
  had `W` as primary and Up Arrow as secondary, consistent with the bounded
  movement press. The live reader check confirmed both binding maps clear
  after sample expiry. The current character has no bindings for many
  secondary action bars, so their occupied slots cannot yet be driven by a
  key. The startup guard remains in place.
- Forever key resolution now requires a live game binding. If a command is
  unbound or the visual snapshot has expired, `KeyReader` refuses to substitute
  a Classic default. A secondary binding is accepted when the primary is
  absent. Synthetic checks cover a bound main-bar slot, an unbound slot, a
  secondary-only slot, and stale data at three UI scales. A live read confirmed
  main slot 1 resolves to `1` while unbound bottom-right slot 49 and stale
  bindings resolve to no key. No action was pressed by this check.
- Forever spell-name action lookup now uses the checked 120-slot action IDs,
  the exact learned spellbook IDs, and current game bindings. It chooses a
  bound slot for the requested spell (and preferred form range when given),
  rejects flyouts, unbound slots, partial grids, and expired captures, and
  never falls back to a similar icon. Synthetic checks passed at three UI
  scales; a live reader check resolved one learned, bound action and rejected
  it after capture expiry. Action-state readers still need the same
  Forever-specific treatment before gameplay.
- Forever profile slot validation now compares the checked action kind and
  spell ID with the learned spell name. A fresh empty slot is distinct from a
  mismatched spell; an unreadable or expired grid is reported as unavailable.
  It does not offer automatic placement while the Forever addon is guarded.
  Synthetic checks cover valid, empty, mismatched, and flyout slots at three
  UI scales; the live reader validated a bound spell and rejected its slot
  after capture expiry.
- The visual probe now exports a checked usability and range state for each
  of the 120 action slots, with each checksum tied to the action kind and ID.
  The reader distinguishes true/false usability, insufficient power, and
  in/out/unknown range. `IUsableAction` uses only a fresh, complete snapshot;
  unreadable or stale state fails closed. Synthetic checks passed at 75%,
  100%, and 125% UI scale, including a corrupted state and an action-ID
  mismatch. A live beta read after `/reload` decoded 120/120 states and 18
  usable actions; a manual fight kept all 120 states readable at 87% player
  and 32% target health. The first range call used the missing legacy global
  and left every occupied slot unknown. The corrected
  `C_ActionBar.IsActionInRange(slot, "target")` call yielded three in-range
  actions in a later live capture. Unknown range remains distinct from false.
  The state sample was rejected after capture expiry. This does not establish
  cooldown readiness, and the startup guard remains.
- A separate 12-row native StatusBar grid now displays remaining cooldown
  fractions for the bound main action bar. Per-slot marker checksums include
  the action ID and kind, so a changed slot invalidates the old fill. Synthetic
  tests at three UI scales cover zero and changing fills, a damaged marker,
  and an action-kind mismatch. After `/reload`, a live read decoded 12/12
  slots; slot 4 fell from 33% to 15% to 3% across read-only captures while
  idle slot 1 stayed at 0%. The sample expires with capture. This is a
  fraction, not a verified millisecond cooldown value, so the gameplay
  cooldown reader still needs a Forever-specific decision rule.
- Forever cooldown gating now requires a fresh, complete action/state/cooldown
  capture, an exact learned spell in the expected slot, a usable action with
  enough power, and an empty cooldown bar. It rejects slots outside the 12-row
  grid and stale or partial captures. `CD_{action}` still means milliseconds on
  Classic. On Forever, numeric reads throw and profile expressions using a
  bound action's `CD_` variable are rejected at load with a clear error.
  Synthetic tests exercise ready, running, wrong spell, unsupported slot,
  missing data, and profile-expression detection at three UI scales.
  The profile audit found four uses that cannot be inferred from a fraction:
  `<= GCD` (Paladin and Death Knight), comparisons between two `CD_` values
  (Paladin), and `>= WAIT_CHARGE_MS` (Warrior). `== 0` and `> 0` express
  ready/busy but still need explicit boolean rules before those existing
  profiles can run on Forever. `CD_MIN_TARGET_HP%` is a user-defined threshold,
  not an action cooldown, and remains valid.
- A checksummed primary power-type pixel now identifies when the blue bar is
  mana. `ManaPercent()` and `ManaMax()` use the fresh visual percentage and
  maximum only for type 0 (mana); other or unreadable types fail closed.
  Exact current health/power remain protected and unavailable. The melee-range
  wait now watches the visual health percentage on Forever rather than reading
  the unsupported exact current health. Profile expressions asking for exact
  mana/energy/rage/focus/runic power or pet/focus health percentages are
  rejected at load. Synthetic tests at three scales cover mana, another power
  type, marker damage, and unsupported expressions. A live beta read after
  `/reload` decoded power type 0 and matching 49% power/mana, then rejected
  the stale capture.
- The visual probe now exports five bag headers and 200 checked item cells
  (40 per bag), including slot count, free slots, bag family, item ID, and
  stack count. `BagReader` refreshes its item list from a complete fresh
  snapshot and refuses stale queries. It does not mark items tradable because
  soulbound, lock, and vendor flags are not in this channel. Synthetic tests
  at three scales cover full inventory, a damaged item marker, adapter counts,
  and stale rejection. An idle live beta read decoded 5/5 headers and 200/200
  cells, with 13 occupied item slots and 15 regular free slots; the reader
  agreed with those counts. A short live fight kept all 200 cells readable at
  82% player and 44% target health, with 14 occupied slots and 14 regular free
  slots; the reader matched the capture and rejected it after expiry. (The
  `BagReader` adapter was removed on 2026-10-03 because no Forever goal reads
  inventory; the grid decoder, its checks and the controller frame gate stay.)
- The Forever dashboard was smoke-tested against the live beta on this machine.
  It returned HTTP 200 and displayed 100% player health, 100% power, and 0%
  target health with no target selected. The log confirmed WGC capture and the
  read-only diagnostic controller. This is a status display, **not a playable
  bot**.
- A vendor visit exposed an independent `SellJunk.lua` merchant event despite
  the main startup guard. The Forever TOC now excludes sell, mail, and trainer
  modules, and those modules have an additional Forever early exit. A manual
  vendor retest produced no new `SellJunk.lua` error. The failing call occurred
  before the module's item-use/sell call.
- The loaded add-on reported client version `20` in SavedVariables. This proves
  package loading, not bot functionality.
- The committed local-only CASC adapter opened Forever's `Azeroth.wdt`
  (FileDataID 775971, 294,988 bytes) and `Azeroth_32_48_obj0.adt` (41,072
  bytes) through numeric and path lookup while `WowB.exe` was running. The
  `--wdt-azeroth` check also passed the source into `MPQTriangleSupplier`,
  parsed the WDT, and found sample tile 32_48 present. An
  earlier local prototype also opened `Kalimdor.wdt` (782779, 294,988 bytes) and
  `Azeroth_32_48.adt` (778027, 477,275 bytes) from the installed client with
  network fallback disabled. The MIT-licensed TACTSharp reader needed a
  file-sharing adjustment to read while WoW was open; that adjustment and a
  stricter local-only guard are now in `external/TACTSharp`.
  `CascGeometryListfile` indexed 585,885 geometry paths from a downloaded
  wowdev community listfile without committing the listfile itself. The
  existing PPather parser accepted the Azeroth WDT and marked tile 32_48
  present. The tile's `_obj0.adt` contains MDDF/MODF placements but no legacy
  filename tables; its first placement IDs were 7549691 and 7962574. PPather
  now supports numeric FileDataID reads for M2 placements and the first model
  parsed as 30 collision vertices and 48 triangles, with all indices in range.
  That M2 uses an `MD21` wrapper around an `MD20` v272 payload. The first WMO
  uses MVER 17 but has a `GFID` group-ID table and modern doodad references.
  A strict WMO reference reader decoded its 290 full-detail groups,
  four LOD sets, 6,752 `MODI` model IDs, and 6,106 `MODD` doodad placements.
  It validates that full-detail group IDs and doodad indices exist; synthetic
  malformed-reference checks pass. The numeric group loader also parses
  modern `MPY2` polygon flags (16-bit flags plus a separate 16-bit material
  ID), which replace the old `MOPY` records. A scan of all 290 full-detail
  groups in the large WMO yielded 992,821 raw triangles with indices in range;
  a smaller one-group WMO yielded 1,752. These are raw faces, not a validated
  walkable/collision-only navmesh. The numeric WMO loader now reads the
  full-detail groups, selected and default doodad sets, model references,
  collision meshes, and transforms. It accepts `MODI` IDs or legacy `MODN`
  names and rejects missing referenced groups or models. Three other roots on
  this tile have no doodad placements; the earlier `MODN` claim was incorrect.
  A read-only in-memory bake of Azeroth tile `126,61` extracted 30,165 ground
  triangles, including 20,129 WMO faces and 4,636 model faces on the sample
  ADT, and produced 561 Detour polygons. A second read-only check compared
  that tile's WMO face centers with the eight MODF placement bounds: all
  20,129 sampled face centers were enclosed. Two adjacent tiles (126,61 and
  127,61) produced 561 and 197 polygons; Detour found a complete 19-polygon,
  four-point path from (-8929.01, -166.40, 81.66) to
  (-8909.28, -104.80, 82.21) across their shared edge. The start is near
  WMO and model collision. This exposed an MCNK culling error: ADT/MCNK axes
  and client-space WMO/model bounds were compared in different coordinate
  systems, leaving zero WMO faces in the start MCNK. With the bounds corrected,
  the small-chunk query retained all 58 nearby WMO faces and all 67 nearby
  model faces seen in the full ADT. This validates one local route and the
  placement bounding boxes, not visual alignment in the game or the collision
  quality of a full continent.
  A read-only liquid check on Azeroth ADTs `32_48` and `30_50` found 2,360 and
  8,182 visible liquid triangles, matching their raw MH2O masks. The latter
  has up to three liquid layers per MCNK. Navmesh tile `126,60` extracted 338
  liquid triangles and baked 11 water polygons. Forever liquid metadata is
  pinned to build `1.60.1.70009`. A readback survey of the full local bake
  found water polygons on 6,566 Azeroth and 8,275 Kalimdor tiles, and
  magma/slime polygons on 19 and 47 tiles respectively. Live lava and slime
  placement still need checking.

## Required work before attempting gameplay automation

1. Extend action readiness beyond the 12 main-bar slots only if the chosen
   Forever profile needs them. A fresh checked ready/busy decision now gates
   learned spells in those slots. Existing profile `CD_` millisecond
   comparisons are audited and rejected on Forever until rewritten as explicit
   readiness rules or backed by a verified timer source. Numeric `Cost` and
   `Cost_...` comparisons are likewise rejected: the Classic action-cost cells
   have no Forever source. For the verified main-bar spells, the checked
   cooldown gate requires a fresh usable action with `NoMana=false`; form and
   combo-point cost rules are rejected until those signals are ported.
2. Finish gameplay data and rules. Player health/power percentages, maxima,
   and checked primary mana percentage are live; exact current values remain
   protected. Provide pet/focus vitals only if the chosen profile needs them.
   Bag IDs, counts, and free slots are live in combat. The beta's aura-list,
   aura-instance, and indexed-aura reads failed during a delayed combat-lockdown
   capture, then worked again after combat. The spell-ID lookup returned nil in
   that capture, which does not establish whether the aura was absent or blocked.
   Forever profile expressions using aura names, aura timer variables, or aura
   post-cast flags now fail at load, and the Classic aura-count pixel is rejected.
   Design and verify a combat-safe aura signal before using those rules. Reject missing data
   rather than reading Classic pixel cells. The 24-bit numeric protocol cannot
   encode protected values as-is.
3. Validate Forever-specific world geometry before gameplay use. The installed
   beta's Azeroth and Kalimdor navmeshes now bake in full locally, and their
   area grids are committed. `BakeTool` and the optional HTTP CASC configuration
   read the installed client; disk-only HTTP pathing also passed. Visual
   placement against the client and broader liquid/collision coverage still
   need validation. The storage refactor builds and passes the navmesh
   coordinate/cost-zone/listfile tests, but its six-tile MPQ byte-identity
   bake could not run here because this workspace has no `Json/MPQ` archive
   corpus. Do not reuse the pre-Cataclysm navmesh: Forever's world is different.
4. The minimal Forever profile and bounded route, action, and recovery tests
   have passed through the ordinary controller at the player's current
   position. This is still a person-assisted trial: the person selects and
   finishes the target. Forever registers only a visual-backed trial GOAP
   goal. Automatic acquisition, pursuit, sustained combat, death recovery,
   looting, and route continuation require their own checked signals and
   bounded tests before the dashboard startup guard can be removed.

### Paladin trial gate

`Json/class/Paladin_Forever_Trial.json` retains Holy Light, Judgement, and
Holy Strike definitions, but the bounded controller trial uses Judgement only.
The profile has no active path, aura rules, loot, mount, or unbound secondary-bar
actions. `Json/path/Forever_Tirisfal_LocalTrial.json` records the older fixed
trial route; the current preflight derives a short path from the player's live
position. Run the read-only combined gate:

```powershell
dotnet run --project Utilities/ForeverVisualReader/ForeverVisualReader.csproj -- --trial-check
```

At the stationary live check on 2026-09-27, the route start was 0.8 yards from
the player, the 12-yard segment resolved entirely from baked mesh, and the
spell IDs, main-bar bindings, action states, cooldown readiness, map position,
mana, and bag grid agreed with the profile. The player was at 100% health and
mana with no target. A later guarded `--trial-move` run sent one 200 ms Up Arrow
press after a fresh full-health/no-target/stationary check; the character moved
1.46 yards over a connected baked mesh segment and remained at full health.
The beta binding grid now contains `STARTATTACK` on Shift+2 and
`INTERACTTARGET` on Shift+3. Checked player identity (level 11 Undead Paladin)
and target flags passed live, including one selected attackable living target.
After the target was cleared, a guarded `--trial-heal` check sent one Holy Light
key and observed mana fall from 100% to 88% without acquiring a target. The
normal Forever controller still refuses gameplay, and its casting path relies
on Classic-only event, aura, and cost data. Those gaps must be resolved before
the startup guard can be removed.

The optional `--trial-judgement` tool originally required a fresh living
attackable target in range, at least 95% health and 60% mana, and checked slot
readiness before one keypress. Its current combat-aware gate is described
below. Two live preflights refused input: the first target was
already dead, and the second found the player at 89% health. An armed run then
held the capture process open until the user's ready signal. Its immediate
preflight refused input at 93% health. A subsequent armed run with a neutral
attackable target passed: one Judgement keypress changed mana 100% to 96% and
target health 100% to 81%; target flags stayed readable. The user finished the
fight and cleared the target, after which the read-only reader smoke found
100% health, 100% mana, no target, and all checked fields valid. The isolated
bounded sequence passes; an autonomous controller-run cycle is still open.

The bounded spell preflight now checks the current game's main-bar binding and
the learned spell, usability, power, and cooldown signals from the same fresh
capture immediately before each keypress. Synthetic tests cover a missing
spellbook and changed or missing bindings. A read-only live gate after this
change reported all trial actions matched, 100% health and mana, and complete
bag data. It refused the near-route condition because the player had moved
298 yards from the original local trial route; no input was sent.

The probe's checked target-flags pixel now also carries player combat and
dead/ghost state, with a version bit that rejects the older three-bit pixel.
`AddonBits.Combat()` and `Dead()` use these flags on Forever and reject an
unreadable or stale sample. A live read during a manual fight found player
combat true, player dead false, 44% health, and a living target at 4% health;
the adapter and stale-sample checks passed. After the fight, a second live
read found combat false, dead false, and 100% health; a living target was
still selected. The idle transition and stale-sample checks passed. A true
death/ghost state has only been checked synthetically. (The `AddonBits`
adapter was removed on 2026-10-03; the Forever goals read these flags from the
visual sample directly.)

A separate checksummed pixel row carries the most recent successful player
spellcast's sequence and exact spell ID. An unreadable event payload clears
the validity marker, and the reader rejects stale captures. Since 2026-10-03
`ForeverEvents.lua` registers the event with `RegisterUnitEvent(..., "player")`,
so another unit's protected unit token can no longer blank the latch; the
unfiltered registration and its checks remain only as a fallback. The bounded heal
and Judgement trials now require a new success event for the expected spell;
mana or target-health movement alone no longer confirms a cast. Synthetic
tests passed at three UI scales, including a damaged checksum. After `/reload`
in the beta client, an idle read showed sequence 0 and spell ID 0. One manual
Holy Light cast changed the read to sequence 1, spell ID 635, while mana fell
from 100% to 93%. The reader also rejected that event after the capture aged
out. In a later armed one-key trial on a living attackable target, Judgement
produced a new successful event for spell ID 20271. Mana fell from 94% to
89%, target health fell from 100% to 81%, and target flags remained readable.
The tool exited after that single keypress; the user finished the fight.
The subsequent read-only recovery check found sequence 7, spell ID 20271,
100% health and mana, no target, and player combat false. All reader and
stale-data checks passed.

The guarded Forever cast path, `ForeverCaster` (first written as a branch
inside `CastingHandler`), handles the three trial Paladin spells. Before a
75 ms keypress it checks the spell, current
binding, usability, range, cooldown, player/target flags, mana, map, and cast
event in one fresh visual capture. It confirms only a new successful event for
the exact spell ID and latches closed after any unconfirmed press until a new
controller session is created. Holy Light remains limited to the verified
no-target, out-of-combat case. The attack spells now require the checked
non-player target identity and level described below, at least 70% player
health, and 60% mana out of combat or 25% mana in combat. Unsupported
post-cast features fail closed. The bounded one-key utilities now call the
same gate. Synthetic checks pass at 75%, 100%, and 125% UI scale for ready,
missing binding/event, dead target, running cooldown, and out-of-range cases.
`ForeverReaderFeed`, called from `AddonReader`, signals the normal
controller's frame wait only once per new, complete visual frame, and the trial player's Horde faction comes from
the checked Undead identity rather than a Classic pixel. Incomplete action,
bag, or event data does not signal a controller frame; synthetic checks cover
those cases at all three scales.
This branch is unreachable from the Forever dashboard while its startup guard
remains. At this stage the normal GOAP state flow and controller-run
route/combat cycle had not been validated or enabled. A read-only trial check
on 2026-09-27 refused
because the live trial's full data/idle conditions were not met; it sent no
input. A later reader smoke found no fresh visual sample during its bounded
capture window. The Debug `Core` and `ForeverVisualReader` builds and the
three-scale self-test passed. A full Release build was not established in this
worktree because existing `dotnet` processes held reference/output DLLs open.

The next controller slice keeps `ForeverDiagnosticController` as the dashboard
startup guard. An explicit `Utilities/ForeverControllerTrial` command constructs
the ordinary `BotController` only for a bounded Paladin trial. The normal
session now registers one Forever goal instead of Classic route, pull, combat,
death, loot, and recovery goals. Its GOAP facts come only from a complete fresh
visual capture; unknown Classic facts remain false and cannot select Classic
goals. The normal no-plan report, XP session persistence, slot texture checks,
macro resolution, screenshot NPC scan, and remote path visualization are
bypassed for this trial. The Forever session (`ForeverGoalFactory`) constructs
none of the Classic casting collaborators.

The trial goal requires the Tirisfal Paladin profile with no saved route, a
stable idle capture followed by a distinct fresh frame, a matching map/world
position, Up Arrow as a verified MOVEFORWARD binding, and a complete three-yard
forward path from disk-only baked navmesh at the current position and facing.
It sends at most one 200 ms forward press, checks the displacement,
waits at most 90 seconds for a person to select a living attackable target,
and sends at most one Judgement through the checked cast gate. It then waits at
most 90 seconds for a combat/dead-target observation and a safe idle recovery.
Missing data, changed combat state, unexpected motion, unconfirmed cast, and
timeouts fail closed. It does not auto-target, chase, sustain combat, heal,
loot, or corpse run. It cannot establish an autonomous Forever bot yet.

On 2026-09-27 the Debug `Core`, `ForeverVisualReader`, and
`ForeverControllerTrial` builds passed (the existing DruidMountHandler pattern
warning remains). The visual self-test passed at 75%, 100%, and 125% UI scale,
including the new Forever GOAP fact check. `ForeverControllerTrial --check`
constructed the normal controller against live WowB PID 21012, loaded the
trial profile, and confirmed that its only registered goal was
`ForeverTrialGoal`; no input was sent. The first read-only route preflight refused
the old fixed route position/facing. A separate live reader smoke found a complete
controller frame, but the character had 62% health and a selected target;
`--trial-check` also refused. At that point no controller `--run` input had
been sent; the later current-position run is recorded below.

The bounded route preflight now derives its short segment from the fresh live
position instead of requiring the historical two-anchor path. A live read at
map 1420 found map/world error 0.05 yard and a complete 12-yard disk-only path
near the character; a separate forward check found a straight three-yard path
with zero new bakes. The old mismatch between the `e3bc14ca` lookup and baked
`e7ad816b` cache came from a stale PPather binary built before the
culture-independent hash fix; rebuilding PPather and the utility restored the
correct cache lookup. The current character's main-bar slot 3 holds Holy Strike
rank 2 (ID 678) rather than the older trial rank (679), so the one-step and
Judgement checks verify only the two spells they use. Sustained combat still
needs a separately verified rank-selection rule.

A live reader smoke later found 190/200 bag slots, while the route/cast signals
remained readable. The bounded trial now signals and reads a frame when its
required position, vitals, flags, action, spellbook, binding, cooldown, and cast
event data are complete. The full controller frame gate still requires all
bags for future inventory work. Three-scale synthetic tests verify that
separation. After the local-route and frame-gate changes, `--trial-check` found
100% health and mana, no target, matching required actions, map/world error
0.02 yard, and a complete current-position route. The ordinary controller's
read-only `--check` also loaded its one goal and passed that local route
preflight without game input.

On 2026-09-27 the controller's read-only preflight passed at the player's
current position. The first `--run` attempt received no complete fresh frame
within its 10-second startup window and sent no input. A second preflight
correctly refused a selected target; after the target was cleared, the
preflight passed. The next bounded `--run` verified one forward step, waited
for the person's target selection, confirmed Judgement's successful cast event,
observed combat, and passed after the target cleared and health, mana, and
combat state recovered. The runner returned exit code 0 with `Controller trial
status: Passed`. The dashboard startup guard remains because this run relied
on a person for target selection and combat.

Remaining for autonomous play: add checked target acquisition and pursuit; add combat-safe spell
selection and repeated action confirmation with low-health escape/healing;
add death/ghost and post-fight recovery; then add loot, route continuation,
and session accounting from Forever-backed signals. Each feature needs a
bounded live test before the dashboard guard is removed. The current trial
still emits warnings for absent Forever creature/talent DBC files while
constructing shared services; these did not prevent the read-only controller
check but cannot be treated as verified mob/skill data.

Run the read-only controller wiring and route preflight with:

```powershell
dotnet run --project Utilities/ForeverControllerTrial/ForeverControllerTrial.csproj -- --check
```

When the client is on the baked Tirisfal mesh, healthy, out of combat, and
without a target, the explicit bounded controller run is:

```powershell
dotnet run --project Utilities/ForeverControllerTrial/ForeverControllerTrial.csproj -- --run
```

The run requires a person to select a safe target after the route step and
finish the fight. It times out and releases its key after the stated limits;
the dashboard remains guarded independently of this command.

The next visual protocol addition carries a target's level, player/non-player
flag, and two independently mixed 24-bit hashes of its GUID. Its checksum
invalidates incomplete or stale rows, and the reader compares presence with
the checked target flags. A new read-only `--target-audit` observes two distinct
fresh frames at the player's current position and accepts only the same
living, attackable, non-player target no more than one level above the player,
with at least 70% player health and enough mana for the current combat state.
The guarded
trial cast gate now requires this identity, and the cast confirmation refuses a
changed or unreadable target identity. This adds a target continuity check;
it does not yet select targets or sustain combat.

After installing the updated `ForeverProbe.lua` and reloading the beta client,
the no-target identity decoded as zero hashes and level zero. A selected
level-8 non-player target against the level-12 Paladin had the same hashes in
two fresh captures and passed the read-only audit on map 1420 without input.
Another level-9 target kept the same checked identity during combat. The first
audit refused it because the initial cast gate incorrectly required an
out-of-combat target at short Judgement range; no input was sent. The gate and
bounded test now accept an in-combat target above the 70% health and 25% mana
thresholds, as requested after the live range check. Out-of-combat attacks
require 70% health and 60% mana. The target must stay the same across two
distinct fresh frames before a trial keypress.
The three-scale visual self-test passed, including damaged identity checksum,
player target, over-level, and below-70%-health cast refusals. A live
`--trial-judgement` run then passed in combat at the current position: player
health was 89%, mana 88%, and the level-8 target was at 4% health. The target
identity stayed unchanged across the two-frame audit and cast confirmation;
Judgement event 20271 appeared, mana fell to 85%, and target health to 1%.
The tool sent one keypress, then the person finished the fight. The updated
controller goal now accepts that in-combat state and requires two stable target
frames before casting. A subsequent `ForeverControllerTrial --check` passed at
the current no-target position. Its bounded `--run` verified the short forward
step, waited for the person's in-combat target selection, confirmed one
Judgement, and returned `Controller trial status: Passed` after the person
finished and cleared the target. The controller run returned exit code 0. The
dashboard startup guard remains because target acquisition and sustained
combat are still person-assisted or unimplemented.

```powershell
dotnet run --project Utilities/ForeverVisualReader/ForeverVisualReader.csproj -- --target-audit
```

For API signatures, use Blizzard's generated UI documentation from the
[`1.60.1 (70009)` UI-source snapshot](https://github.com/Gethe/wow-ui-source/tree/bd2470aed543f72697a044e989285b6c83e63f73/Interface/AddOns/Blizzard_APIDocumentationGenerated)
and validate behavior in the actual beta client; the client may protect a
value even when its documented return type is `number`.

## Autonomous controller work, updated 2026-09-28

The new guarded `Paladin_Forever_Auto.json` profile constructs
`ForeverAutonomousGoal` through the normal `BotController`. Its collaborators
are separate target, navigation, combat, and loot components, with session policy
in `ForeverRunOptions`. It chooses an opponent among the enemy nameplates and
clicks it (see "Finding, selecting and facing targets" below), locks that
opponent through pursuit and combat, holds forward while steering with the right
mouse button, and uses checked spell range to stop. Seal (slot 5), Judgement
(slot 2), melee start and Holy Strike (slot 3) are enabled. `Loot` is now true.
This profile remains available through the explicit bounded utility;
`ForeverDiagnosticController` still guards dashboard startup.

Since 2026-10-04 the run is a selector over activities (see "Activities and
the agenda" below); until then it was one state machine with the phases Start,
Search, Select, Approach, Fight, Loot, Rest and Recover. Each activity runs on
its own explicit state machine (`ForeverStateMachine`, since 2026-10-03),
which declares every state once: its work on a frame, the states it may move
to, its hard time bound (searching 45 s, Select 8 s, Approach 20 s, Fight
75 s, a loot round 120 s; Select's and Approach's hand over to a setback
instead of failing), the hooks that release what it holds, such as held
movement when pursuit ends, and what it allows besides its work
(`ForeverAllowed`): a routine several states share, such as walking, does only
what the current state allows. A move the declaration does not allow ends the
run instead of acting on a bug; a bound is checked before the state's work on
each frame. The bounded trial goal runs on the same class. Its checks,
together with the selector, rotation, preflight and key checks, run offline:

```powershell
dotnet run --project Utilities/ForeverVisualReader -- --logic-test
```

Read-only current-position preflight:

```powershell
dotnet run --project Utilities/ForeverControllerTrial/ForeverControllerTrial.csproj -- --auto-check
# One complete kill / loot / recovery cycle:
dotnet run --project Utilities/ForeverControllerTrial/ForeverControllerTrial.csproj -- --auto-run
# Repeat cycles until a full look-around finds no eligible nameplate; no roaming:
dotnet run --project Utilities/ForeverControllerTrial/ForeverControllerTrial.csproj -- --sweep
```

Tests start at the current player position, without a fixed route origin. The
single-cycle utility has a 170-second total bound; the sweep has a 600-second
bound and stops once a full look-around shows no eligible nameplate. Each
pursuit remains bounded to 35 yards / 20 seconds. The controller reports normal
completion separately from failures. A run waits up to 60 seconds for the
preflight (combat over, health and mana back) and then for the game window to
be active: the run steers and clicks with the mouse, and Windows does not let a
background process take focus right after someone typed elsewhere.

The first attempts exposed several defects now fixed: marker detection had been
cropped to the left 650 pixels; mouse focus changes could cancel a forward key
pressed beforehand; combat entry could trigger another Tab; and a target could
clear before its dead flag reached the next screenshot. Capturing the entire
viewport, taking focus before key-down, retaining the acquired identity, and
latching kill events address those cases.

Every key the runtime presses comes from the client's own bindings, read from
the binding grid on the frame it presses on: move forward, strafe, start
attack, interact and the main-bar buttons. Rebinding one in the game needs no
change here. Clearing the target is a macro rather than a bindable command, so
the profile names its key with upstream's syntax, for example
`"ClearTarget": { "Key": "7" }` for a `/cleartarget` macro on main-bar slot 7.
That key is pressed only while the client binds it to a macro on the main bar;
otherwise upstream's clear-target binding when the client has one, then Escape.
Any of them is pressed only while the frame shows a target and at most once a
second: with nothing selected Escape opens the game menu.

Local movement uses a Detour raycast through connected polygons, returning the
height at the endpoint. It no longer rejects ordinary slopes solely for a
1.5-yard height change or independently resolves the endpoint onto another floor.
With `NavmeshQueryOptions.PreferGroundUnderPoint`, which only the Forever
navmesh turns on, the column resolver prefers ground under the requested X/Y
over a higher surface beside it; Classic clients keep the upstream behaviour. Blocked straight movement tries checked sidesteps and pathfinder
waypoints while retaining the same target.

Movement is human-like since 2026-10-03. Roaming, approach, detours and
pursuit share one walking routine: forward stays held while the right mouse
button steers toward the heading, and the next three yards along the current
facing are re-checked on every frame. The character turns standing only for a
correction over 90 degrees, a pursuit marker more than 35% of the screen off
center, or a way ahead that is not walkable. Held movement must cover 0.2 yards
every 3 seconds. Roaming stops when a candidate appears. The earlier roam tapped forward for 200 ms (or a turn key
for 45 to 120 ms), stopped to verify, and repeated. Key taps for casts and
targeting last 60 to 90 ms at random instead of a fixed 75 ms. The one-step
`--trial-move` test still sends its single 200 ms press by design.

Live target-position audit on a selected level-6 mob found both
`UnitPosition("target")` and `C_Map.GetPlayerMapPosition(mapId, "target")`
unavailable, while the corresponding player positions work. A one-press
`INTERACTTARGET` approach did not move the character. `ClosestUnitPosition`
returned no usable result for the selected creature, and
`UnitDistanceSquared("target")` returned zero for a visibly distant mob, so
the latter is rejected (both probes were removed from the add-on on
2026-10-03). A normal nameplate frame list did contain the selected
mob: `C_NamePlate.GetNamePlates()` plus `frame:GetUnit()` and `UnitIsUnit`
matched it, though `frame:GetCenter()` gave no usable numeric point.

The add-on anchors a magenta/cyan visual marker with a black interior to that selected
nameplate. A saved live 1920x1080 capture decoded its position as normalized
screen X 0.548, Y 0.915. The reader matches the marker only when the same
capture contains a checked target identity and a matched nameplate parent;
its three-scale synthetic test passes, including a marker beyond X=650. Screen
guidance remains conditional on nameplate visibility. The navigator uses marker
X for mouse steering, Judgement range for the initial cast, then Holy Strike
range for melee. This passed the live controller cycle below. The add-on cannot
recover those coordinates from its protected nameplate frame API. Since
2026-10-03 the target's marker is the nameplate roster's (below); the
`--watch-nameplates` overlay labels every marker with its screenshot-derived
position.

### Death and loot events

`ForeverEvents.lua` observes `PLAYER_TARGET_DIED` and `PARTY_KILL`, deduplicates
them by GUID, and latches a sequence plus the victim's two identity hashes. A
matching new event proves death even if the current target is already empty.
The live record identified `PARTY_KILL` as the source. `LOOT_OPENED`,
`LOOT_SLOT_CLEARED`, and `LOOT_CLOSED` similarly persist across captures.

Until 2026-10-04 `IForeverLootRoutine` restored the corpse with
`/targetlasttarget`, pressed the bound `INTERACTTARGET` from where the fight
ended and typed `C_LootFrame.TryAutoLoot()` if the window stayed open. It now
runs a loot round after the fight; see "Looting after the fight" below. A
round still requires every tracked slot to clear and the window to close.
It changes no client setting.

Definitions: [target death](https://github.com/Gethe/wow-ui-source/blob/forever/Interface/AddOns/Blizzard_APIDocumentationGenerated/PlayerScriptDocumentation.lua),
[party kill](https://github.com/Gethe/wow-ui-source/blob/forever/Interface/AddOns/Blizzard_APIDocumentationGenerated/UnitDocumentation.lua),
[loot events](https://github.com/Gethe/wow-ui-source/blob/forever/Interface/AddOns/Blizzard_APIDocumentationGenerated/LootDocumentation.lua),
[Loot All](https://github.com/Gethe/wow-ui-source/blob/forever/Interface/AddOns/Blizzard_APIDocumentationGenerated/LootFrameDocumentation.lua).

### Live results and remaining work

- A complete autonomous cycle passed on 2026-09-28: one mob killed, its identity
  confirmed after target clearing, two loot slots collected, and recovery completed.
  The following read-only capture showed death sequence 1, two cleared slots,
  zero remaining slots, a closed loot window, and full player health/mana.
- The first `--sweep` then killed and looted one more mob (one loot slot), returned
  to Tab acquisition, received no target twice, and finished with `status: Complete`.
  Log: `%TEMP%/forever-sweep-20260928.log`.
- A second sweep also completed one kill and loot, then stopped on no target
  (`%TEMP%/forever-sweep-20260928-2.log`). The final requested sweep completed
  **two consecutive kills and two corpse loots** before Tab returned no target.
  Its loot events cleared four slots total (one then three). Holy Light event 635
  confirmed recovery from 88% to 100% health between those fights. The final
  status was `Complete`, exit code 0, with 100% health and 81% mana.
- Builds of the controller and visual utilities pass. The three-scale self-test
  covers full-viewport markers, Seal icons, death after target clearing, stale or
  wrong-target deaths, damaged event checksums, and stale/partial loot completion.
- Missing optional Forever creature/talent DBC files now log at Debug; malformed
  files and missing files on other clients retain their existing handling.

Remaining before general autonomous dashboard use: reliable Seal visibility
through restricted combat, broader handling of additional attackers and target
loss, player death/corpse recovery, and route/travel/vendor integration. Aura
enumeration (`GetUnitAuras` and `GetAuraDataByIndex`) was blocked by the client in
combat. An unknown Seal state therefore withholds Seal/Judgement and continues
melee, rather than repeatedly refreshing an already active buff. Out-of-combat
icons verify Seal presence and prevent duplicate presses. The 500ms action grid
includes GCD, and casts wait for its refresh before another decision. In-combat
health below 70% requests a self-heal rather than abandoning the fight. Holy
Light recovery between fights is verified; healing while an attacker is still
active needs a separate live case.

### Slot aura references (2026-10-03)

The aura signal is no longer tied to Seal of Righteousness. Beside the 40
player aura icons, the add-on draws one reference icon per main-bar slot
(`GetActionTexture(1..12)`), and `VisualAuraReader.Read` reports, per slot,
whether an aura shows that slot's icon (`VisualBarSample.SlotAuras`). An empty
slot reads as unknown, and the whole snapshot is unknown while aura enumeration
is blocked, as before. The aura state cell moved from 2 to 3, so a capture from
the previous add-on is refused rather than misread; reload the add-on together
with the bot. The self-test covers matching, non-matching, empty and
out-of-frame references. The live Seal check has to be repeated with this
layout, because the reference now comes from the action button texture rather
than `C_Spell.GetSpellTexture`.

### Nameplate probe (2026-10-03)

Acquisition will move from Tab to the enemy nameplates: every attackable unit
the client draws a plate for, seen without selecting it, then targeted with a
click on its plate. `/foreverprobe` snapshots (world, combat +0.5 s, combat
+3 s, after combat, manual) now record a `nameplates` section with each API's
status, and event latches count plate additions, behind-camera changes, plate
hovers and clicks that selected the hovered plate. Names and GUIDs are recorded
as types only. On build 1.60.1.70205, a level 7 Hunter in Durotar:

- Plates exist for units that are not targeted (`nameplateShowAll` 1, the
  default; `nameplateShowEnemies` 1, set in this client) out to
  `nameplateMaxDistance` 45 yards. The world snapshot right after a reload has
  none yet; they are added a moment later.
- In and out of combat a plate's unit returns ordinary values for `UnitGUID`,
  `UnitCreatureID`, level, `UnitCanAttack`, `UnitIsDead`, `UnitIsPlayer`,
  `UnitReaction` (2 hostile, 4 neutral), `UnitIsTapDenied`, `UnitIsUnit`
  against the target, and `IsNamePlateUnitBehindCamera`. The behind-camera
  event carried a readable flag every time (12 of 12).
- The mob's own combat state lags the player's: 0.5 s into the fight its
  plate reported no combat, no threat and no target; at 3 s it reported
  combat, `UnitThreatSituation` 3 and `<plate>target` = player.
- `C_ActionBar.IsActionInRange(slot, plate)` and `C_Spell.IsSpellInRange`
  answer per plate and agree: a far mob read out of range for Auto Shot,
  Serpent Sting and Arcane Shot while Hunter's Mark stayed in range. Raptor
  Strike again reads in range at any distance; the Attack slot reads nil.
- `GetCenter` on a plate errors and `UnitPosition` returns nil, as for the
  target. Anchoring a hidden frame to a non-target plate was accepted, also
  in combat.
- A plate's alpha is readable and follows the client's fade settings:
  0.6 (`nameplateMinAlpha`) to 1.0 with distance, times
  `nameplateOccludedAlphaMult` 0.4 when the camera cannot see the unit. One
  plate read 0.24 (0.6 × 0.4, far and occluded); the others read 0.6 to 1.0.
  In combat and for the target it read 1.
- Hovering a plate made it the mouseover unit (28 times), and seven times a
  left click on the hovered plate selected that unit. `deselectOnClick` is 0,
  so a click that misses keeps the current target; `autoInteract` is 0.
- `softenemy` existed only in combat; `SoftTargetEnemy` is 1 and soft
  targeting is not used.

### Nameplate roster (2026-10-03)

`ForeverNameplates.lua` follows `NAME_PLATE_UNIT_ADDED`/`REMOVED` and gives
each attackable plate one of eight slots; the target always gets one, taking
the slot held longest when all are used. Each slot gets a new incarnation
number whenever it takes a unit, and a marker anchored at its plate's centre:
the old target locator's magenta square and black ring, with the 6-unit centre
coded per slot (red 16 + 32 per slot, green and blue full). The markers sit
below the telemetry panel, which moved to the `TOOLTIP` strata so a plate in
the corner can no longer cover a telemetry pixel. A marker is hidden while
its plate is behind the camera or could not be anchored.

`ForeverVisual.lua` writes a checksummed header (plate counts, the
`nameplateShowEnemies`/`nameplateShowAll` settings, `nameplateMaxDistance`
and the roster version) and one row of seven values per slot beside the event
latches: incarnation, GUID hashes (0 while protected), level and tri-state
flags (attackable, dead, player, tapped by another, in combat, target,
mouseover, behind camera), then targets-player, reaction, threat and plate
alpha, then each main-bar slot's `IsActionInRange` to the plate in the
action-state codes. A row naming the target is checksummed with the checked
target hashes. `VisualNameplateReader` decodes it into
`VisualBarSample.Nameplates`, and `VisualMarkerLocator` finds every slot's
marker in one full-viewport scan (about 1.3 to 2 ms per 1920x1080 frame:
a stride-2 search for the centre colour, then a full-resolution check around
each hit). `TargetScreenPosition` is the target slot's marker; the single
target locator, its probe pixel and the unused Lua screen-point cells are
retired.

Live on the Hunter in Durotar, `--watch-nameplates` labelled up to four
plates at once and followed them while the camera turned and the character
walked; ranges changed per plate as it approached, a hovered plate read
`hover`, and a click on it made it `target` (the step automated next). A
fought mob read `attacker`, and a far one `occluded`. Plates exist only for
units in the camera's view: turning away removes a plate rather than flagging
it behind the camera, and turning back adds it again under a new incarnation.
The roster is therefore a census of what the camera sees, out to 45 yards,
and finding enemies around the character means looking around. A marker
reaching the screen edge is partly clipped and reads as not found. The
controller cycle passed with pursuit steering on the roster's target marker.

Offline, `--self-test` covers the header, rows, target linkage, corrupt and
other-version rows, three markers and a duplicate at three UI scales. A
scratch check ran the old and new add-on against one mocked API (all 1,775
existing telemetry entries identical in four scenarios) and decoded the new
panel with `VisualBarReader`, so the Lua encoding and the C# decoding agree.

### Finding, selecting and facing targets (2026-10-03)

Tab is no longer pressed. The run finds its opponents among the nameplates in
view and clicks one, as a person does:

- **Search.** `ForeverTargetPolicy` decides which units may be fought (an
  attackable, living non-player not tapped by someone else, inside the
  profile's `NPCMaxLevels_Above`/`NPCMaxLevels_Below` window and, unless it
  fights the player, of a kind the profile's `TargetMask` allows; the
  selected unit passes the same rules) and which plate to take: one whose
  marker is on screen, not set aside, preferring an attacker, then the current target, a
  unit the camera can see, one an opener reaches, one a Combat spell reaches,
  then the lowest on screen. In combat only units that fight the player
  count, set aside or not.
  The rotation's ranges come from one place: `ForeverSlotRanges` is either the
  target's action states or a plate's ranges, and `Reach` gives opener range,
  combat range and too-close-to-open for either. With nothing to take, the
  character looks around standing, one full circle, then roams a stretch and
  looks again; a sweep completes after a look-around with nothing to take.
- **Select.** `ForeverPointAndClick` aims at an `IForeverClickTarget`: the
  cursor goes onto it in one swift eased stroke, aimed ahead of a moving
  thing along its screen velocity, and the click comes the first time the
  game reports it under the cursor once the cursor has rested 120 ms (the
  add-on samples the hover ten times a second), moving or not. It is done
  once the game shows the effect: for a nameplate, the plate reporting itself
  the target. A click without effect is retried once.
- **Recover.** A setback with one unit (`ForeverSetback`: it could not be
  selected, its marker stayed out of sight, held movement toward it went
  nowhere, the way was blocked, or it stayed out of reach) sets it aside
  (`ForeverExclusions`, by GUID hashes; a minute, two for a bad way) and
  searches again, as a person gives up on a mob across a ravine.
  `ForeverRecovery` allows five setbacks without a kill; in combat only a
  selection is retried, so other setbacks while fighting still stop the run.
  The navigator reports these kinds instead of reason strings, and being stuck
  while roaming just means looking around again.
- **Facing.** Attacking implies facing. The combat routine asks
  `IForeverFacing.FaceTarget` on every frame of the fight and while a cast is
  awaited, and holds every targeted action until the target is in front. The
  turn leads a moving marker by its screen velocity (0.15 s), so a mob running
  across the view is kept up with, and the band in front is wider to leave
  than to enter, so the camera does not twitch at its edge. A target that left
  the view is turned straight to the bearing it was last seen at, from the
  exact character facing and the marker's offset under a 90-degree field of
  view, then looked for further round, for one circle at most. The chase
  steers with the same lead.

The movement pieces are reusable for anything else the runtime will click or
turn toward: `IMousePointer`/`MousePointerInput` (the eased, slightly arced
stroke and the click), `MouseCameraInput` (right-button turning at a steady
speed proportional to the error, pumped in small steps several dozen times a
second, up to 1,200 pixels a second, pressed where `ForeverDragPoint` finds
empty ground), `IForeverFacing`, and `ForeverPointAndClick` with an
`IForeverClickTarget`.

Preflight now requires the roster and enemy nameplates that are always shown,
and no longer the Tab binding. Live on the Hunter in Durotar: the first click
run ended in the fight on the known Raptor Strike limitation; the bot had
opened on a mob at the screen edge without turning to it, which led to the
facing rule. After facing (with a faster turn) and facing during cast waits,
a sweep killed a mob, set aside one it could not select, looked around and
completed with exit code 0; the person watching called the aim, click and
turns indistinguishable from a player's. Fleeing mobs were lost from view
until the lead and the bearing turn; then the camera kept up with them. A
click on a walking mob waited for it to stop until the click moved from
"cursor within 3 pixels of the marker" to "the game reports it under the
cursor"; the last cycle selected, faced, killed and rested cleanly.

### Melee reach and attack state (2026-10-03)

The Hunter pressed Raptor Strike from range: its slot reads in range out to
about 100 yards, so its range used to count from the start of combat. A
next-swing press out of melee reach is never queued, and the run stopped on
"Raptor Strike success was not confirmed". `/foreverprobe` now records a
**range trace** whenever the player has a hostile target, living or a
corpse, or is in combat: whether the target is dead and lootable
(`CanLootUnit`), interact distances, each main-bar slot's range to the
target, its current and auto-repeat state, melee, Auto Shot and the player's
spell and attack events, one row per change, the last five fights kept in
`ForeverProbeResults.rangeTraces` (written on `/reload`). On build
1.60.1.70205, a level 8 Hunter fighting by hand in Durotar:

- `C_Spell.GetSpellInfo` gives each spell's own range: 0 to 0 for Raptor
  Strike and Attack (melee), 8 to 35 for Auto Shot, Serpent Sting, Arcane
  Shot and Concussive Shot, 0 to 100 for Hunter's Mark.
- `CheckInteractDistance` answers in and out of combat, for the target and
  for nameplate units. Duel distance (3), like 2 and 5, turned true in the
  same 0.1-second sample in which the ranged slots went out of range as the
  mob crossed the 8-yard minimum. Indexes 1 and 4 (28 yards) turned true
  earlier.
- Serpent Sting started Auto Shot by itself. As the mob crossed the
  minimum range, the client stopped Auto Shot (`STOP_AUTOREPEAT_SPELL`) and
  turned melee on (`IsCurrentSpell(6603)`) in the same sample, without a
  key, followed by `PLAYER_ENTER_COMBAT`.
- Raptor Strike pressed in melee showed as current on its slot
  (`IsCurrentAction`) and succeeded with the swing 0.2 seconds later.
- The current, auto-repeat and melee states read in combat; unit speeds
  are secret in combat.

The panel carries this as an **attack state** (`VisualAttackState`, cells 17
to 19 of the target probe row): each main-bar slot's current and auto-repeat
bit, melee, and whether the target is within duel distance, checksummed with
the checked target hashes once that distance is known. Each nameplate row
carries the same distance flag (`VisualNameplate.Close`; roster version 2).
`ForeverSlotRanges.Close` gives it for the target or a plate alike, and the
runtime uses it in three places:

- **Next-swing reach.** A next-swing ability's range counts only while the
  unit is close: inside a ranged weapon's minimum range, where an attacking
  mob is in contact or a step away. At range the shots and Auto Shot run on.
- **Start attack.** `AutoAttack` presses `STARTATTACK` when no auto attack
  runs, or when the target is close and melee is off, at most once a second;
  at range that starts Auto Shot, up close melee. The client mostly switches
  by itself, and a press then changes nothing. An unreadable attack state
  presses nothing.
- **A press that is pending or not taken.** A next-swing press counts as
  sent once its slot shows current; it is not pressed again while pending,
  and its success event arrives with the swing. A press that leaves the slot
  unchanged for a second was not taken; it is tried again after 1.5 seconds.
  Neither latches casting closed. Without a readable attack state the caster
  waits for the success event and fails closed, as before.

The autonomous preflight refuses a panel without the attack state; reload the
add-on together with the bot. A mob that stops inside the minimum range
without coming into contact (it cannot reach the player) was not handled
then: the Hunter could neither shoot nor swing at it. The game's "You are too
far away!" now makes the fight step in; see "Ported from upstream" below.

Live (level 9 Hunter, Durotar): an `--auto-run` cycle opened with Serpent
Sting, kept Auto Shot running with Arcane Shot at range and did not press
Raptor Strike there. As the mob closed in, the attack state showed it close,
melee on and Auto Shot stopped; Raptor Strike landed twice, the kill was
verified and the corpse looted, and the run completed with exit code 0.

**Camera drags start on empty ground.** The first sweep after that stopped
when the target switched mid-fight to a far mob that was not fighting. The
range trace showed `PLAYER_TARGET_CHANGED` together with melee starting on
the new unit, as a right-click does. The ranged slots had gone out of range
0.1 seconds before the target turned close, the fight handed over to
pursuit for that moment, and pursuit pressed the right button at its fixed
point a third down the screen, over that mob's nameplate, and let go again.
A controlled test confirmed it: a right-button press on a nameplate targets
the unit and starts melee on it when the button comes up, even after moving
the mouse 8 pixels and back during the hold. So where a hold begins matters,
not what the mouse does during it:

- `ForeverDragPoint` picks the press point for every new hold: the first of
  eight points in the world view clear of every roster plate, the body below
  it and the character. With none clear, the turn waits a frame.
  `MouseCameraInput.Turn` presses where it is told.
- The fight leaves for pursuit only once reach stayed lost for a second
  (`ReachLostMs`): the action rows refresh every 0.5 seconds and the target
  distance every 0.1, and a closing mob crosses the minimum range between
  them.

`--logic-test` has 119 checks.

### Looting after the fight (2026-10-04)

Looting stopped working once kills happened at range: of six corpses after
click targeting, none was looted. The body lay 8 to 35 yards away, the
routine pressed interact from where the fight ended, `CanLootUnit` read false
and after a second the round was called empty. The ranged corpses were
lootable, only out of reach; a sweep on 2026-10-04 looted the body killed in
melee and not the one killed at range. The client also clears the target
when it dies, and a dead unit's nameplate fades out within about 0.3 seconds,
so a body has no marker to steer by or click.

Looting is now its own phase after the fight, as a person plays:

- **Kills are remembered, not looted.** `ForeverCorpses` notes on every frame
  where the target and every plate with an identity and a marker are: the
  player's position, the bearing to the unit (`ForeverNavigator.BearingOf`,
  the facing turned by the marker's offset under the 90-degree field of view)
  and how far it can be (0 to 8 yards when it reads close, 8 to 28 within the
  client's follow distance, else 28 yards to the plate distance, 45; the
  follow distance came with roster version 3, below). A new death event of a unit seen in the last 10
  seconds becomes a body on that line. A kill while something else still
  fights the player sends the run back to Search, which takes attackers only.
- **Once combat is over, a loot round** (`ForeverLoot`) visits the bodies
  nearest first: it walks the body's line on the navmesh
  (`ForeverNavigator.WalkToward`), to its near end and on to its far end,
  until the client's **Interact Key** finds the body. The soft-interact unit
  being dead and lootable is the signal (`VisualSoftInteract`, a checksummed
  block beside the death latch). Then the round presses the bound
  `INTERACTTARGET`, and the client's **Auto Loot** empties the window and closes
  it. Any lootable body the key finds on the way is taken too. A selected
  unit is cleared first, since the key acts on it before the soft target. A
  body not found by the far end, or within 30 seconds, is given up; the round
  as a whole has two minutes. An attack during the round sends the run back
  to fighting, and the bodies wait for the next round.
- No chat command is typed: `/targetlasttarget` and
  `C_LootFrame.TryAutoLoot()` are gone from looting.

A looting profile needs two client settings, which the preflight checks: Enable
Interact Key (`SoftTargetInteract` 3; the client's default 1 is gamepad only)
and Auto Loot (`autoLootDefault`). The `/foreverprobe` range trace logs the
soft-interact unit, so a live run shows when the key finds a body.

Offline, `--logic-test` (137 checks) covers the ledger (a melee kill within 8
yards ahead, a ranged kill beyond, a marker at the screen edge 45 degrees
aside, a plate's unit, a death never seen, nearest first, bodies given up
after three minutes) and the round (combat, clearing a selected unit, the key
on a lootable body, the walk to the near and far ends, a window emptied and
closed, a window closed with items left). `--self-test` covers the new block
at three UI scales, and a scratch run of the committed and working add-on on
one mocked API kept all 1,838 existing telemetry entries identical. Not yet
verified live.

### Ported from upstream (2026-10-04)

A comparison with upstream's Classic goals (`PullTargetGoal`,
`ApproachTargetGoal`, `CombatGoal`, `LootGoal`, `Navigation`, `StuckDetector`,
`ReactCastError`, `Blacklist`) found the looting design to be upstream's first
choice already, and its movement and targeting less human than ours: it walks
with Click-to-Move, turns with timed arrow keys and finds mobs with Tab. Six
things upstream does were worth taking, and Forever's data allows each:

- **The game's error messages.** `UI_ERROR_MESSAGE` text is readable on this
  client (the range traces show "Target too close" and "Spell is not ready
  yet."). `ForeverEvents.lua` latches the messages the runtime reacts to,
  matched against the client's own global strings as upstream's add-on does,
  as a sequence and a kind (`VisualGameError`), in three checksummed cells
  after the loot latch (row 225, cells 26 to 28; `VisualErrorReader`). A new
  message after a press, with no success event within half a second, ends
  the wait at once as `ForeverCastResult.Refused`, which leaves casting open
  and tries that action again after 1.5 seconds. Before, the press waited
  three to five seconds and then stopped the run. The fight reacts as
  `ReactCastError` does (`ForeverRotation.ReactionTo`): out of range or "You
  are too far away!" steps in at once; out of sight before the fight began
  sets the unit aside, in combat it steps in; anything else is tried again.
  Melee that is on while the client says "too far away" steps in as well,
  which handles the Hunter dead zone above. A step holds the fight back until
  the character moved 3 yards or 3 seconds passed; four in a row on one
  target give it up as out of reach.
- **Attacked while approaching another unit.** In combat, when the selected
  unit's row shows it neither has the player on its threat table nor targets
  the player, and another eligible plate attacks
  (`ForeverTargetPolicy.AttackedByAnother`), the approach drops it and Search
  takes the attacker, as upstream's approach clears a target that is not in
  combat. Search no longer fights such a passive selection as it is. A unit
  set aside earlier is still taken once it attacks, as upstream never
  blacklists what damages the player.
- **Elites.** Roster version 3 adds each plate's classification, and plates
  outside the profile's `TargetMask` (upstream's default: normal, trivial and
  rare) are passed over unless they attack. An unknown classification passes.
- **Where a body lies.** Version 3 also adds each plate's follow distance
  (`CheckInteractDistance` 4, 28 yards, which answers in combat), so a ranged
  kill lies 8 to 28 or 28 to 45 yards out instead of 8 to 45. Upstream places
  a body at the middle of a range bracket from LibRangeCheck; Forever's
  bearing is better than upstream's, its distance still coarser.
- **Looking for an attacker.** In combat with no attacker in view, Search
  turns at full speed instead of the calm look-around speed.
- **Hops.** Held movement that made no progress for 1.5 seconds taps the
  client's jump key once, the first rung of upstream's unstick ladder; three
  seconds without progress is still a setback. Roaming also hops now and then
  for no reason, as upstream jumps at random while it walks: every 4 seconds
  of walking a roll with a one-in-four chance starts one to three hops in a
  row, each 0.65 to 0.9 seconds after the last, as the character lands
  (`ForeverHops`). A roaming stretch lasts 8 seconds, so about one in four
  has a row. Each phase declares which hops it allows: Search snag and idle
  hops, Approach and Loot snag hops only, the others none, so Fight never
  hops.

One fix came out of reading `Navigation`: `WalkToward` checked 3 yards
straight ahead even when its waypoint was closer, so a loot walk whose end lay
near an edge could stand still until the body's 30 seconds ran out. It now
checks only as far as the waypoint, as `FollowDetour` already did. Checking
every move against the declaration found one undeclared: Select went back to
Search when it had no chosen unit, which cannot happen; it now stops the run
with that reason.

**No chat commands.** The runtime no longer types anything into chat. A
target that vanished during a fight was restored with `/targetlasttarget`
for a death check; the death latch makes that check, so the fight now waits
1.5 seconds for it and then goes back to Search, which takes whatever fights
the player by its plate, the same unit included. A profile `MacroText` is
refused, and the Paladin's Holy Light is its key with Auto Self Cast.

The add-on and the bot must be reloaded together: the reader refuses roster
version 2. `/foreverprobe` also records `SoftTargetInteractArc` and
`SoftTargetInteractRange`, how far and how wide the Interact Key looks for a
body.

Offline, `--logic-test` has 183 checks (the reactions, the step-in window and
its count, the attacker during approach, elites, the follow-distance band, the
hops and which phases allow them) and
`--self-test` covers the error latch and the version 3 rows at three UI
scales. A scratch run of the committed and working add-on on one mocked API
kept all 1,825 other telemetry entries identical and decoded the new cells end
to end, including a protected and a failing classification. None of it is
verified live yet: whether this client sends "You are too far away!" for melee
out of reach, and the exact text of its line-of-sight message, show in the
range trace's `UI_ERROR_MESSAGE` rows.

Not ported:

- Click-to-Move approach and looting, and `/targetlasttarget`: the
  straight-line movement and chat commands Forever leaves out on purpose.
- Food and drink between fights: `ForeverCastGate` refuses item slots, and
  what eating reports on this client (its success event, its buff icon) needs a
  live probe first.
- Walking a route and scanning while walking: upstream's generated routes
  need spawn data Forever does not have.
- The rest of the unstick ladder (sidestep, back out), a corpse search with
  the mouse, and preferring mobs that stand alone: worth doing if live runs
  show the need.
- LibRangeCheck brackets: DataToColor does not activate the library on
  Forever, and whether item ranges answer for enemies in combat is unprobed.

### First live run of the ports (2026-10-04)

Level 9 Troll Hunter in Durotar, add-on and bot reloaded together. The
start-up check passed with roster version 3; the plate rows read live (distance
bands, classifications) and so did the error latch. One `--auto-run` cycle
verified a kill, then stopped on "game lost focus during mouse steering": a
click outside the game while the camera turned, which stops a run by design.

What went wrong, from the range trace:

- **The Hunter never shot.** Auto Shot, Serpent Sting, Arcane Shot and
  Concussive Shot read unusable for the whole fight while Hunter's Mark and
  Raptor Strike were usable, though the quiver held 1,400 Sharp Arrows: the
  ammo slot was empty or the bow missing. The rotation, finding nothing to
  press at 35 yards, pressed Start Attack, which without a usable shot starts
  melee, and crept in as the range flickered. Now a spell the client reports
  unusable for a reason other than mana reaches nothing, so the fight closes in
  to what it can use, and the start-up check and every new pull stop the run
  naming the action (`IForeverCombatRoutine.TryAttacksUsable`).
- **It spun after the kill.** Combat lingered about 17 seconds with nothing
  attacking, and Search looked round at full speed. Danger is now losing health
  or an attacker in view while in combat (`ForeverDanger`, upstream's
  dangercombat from the health bar). Without it, Search stands still and the
  loot round runs while combat lingers; with damage and no attacker in view it
  turns round at half speed. Rest goes back to Search when attacked, counting
  only damage taken since the rest began.
- **A dead unit was locked again.** The death latch arrived a frame before the
  target's flags, and Search fought the corpse "as it is". A unit the death
  latch reported is never taken again (`ForeverTargetPolicy.ReportedDead`).

The client sent "Spell is not ready yet." (61) when Raptor Strike was pressed
right after its swing, and "You are facing the wrong way!" (282) once; both are
now refusals tried again. It sent no "You are too far away!" while melee was on
out of reach, so the melee step-in from that message has not been seen to fire.
`ForeverVisualReader --bags` prints the bag contents and each main-bar action's
usable state.

### The Hunter's pet (2026-10-04)

The runtime did not know about the Hunter's pet. The lingering combat in the
first run was most likely the pet fighting another mob, and nothing sent the
pet in. A person sends the pet first, shoots, and turns on whatever goes for
the pet.

- **What the add-on reports.** A checksummed pet block after the roster
  header (row 245, cells 22 to 25; `VisualPetReader`, `VisualBarSample.Pet`)
  holds whether the pet exists, is dead and is in combat, the identity hashes
  of the unit it attacks (`pettarget`; 0 with none or while protected), and its
  version. Roster version 4 adds an eighth cell to each slot row: whether the
  plate's unit targets the pet (`VisualNameplate.TargetsPet`).
- **Defending it.** A unit that targets the pet counts as an attacker
  (`VisualNameplate.Attacks`). Search takes it in combat, danger counts it,
  Rest gets up for it, and an approach to a passive unit is dropped for it.
- **Sending it in.** When the fight can act on its target and a living pet
  attacks nothing or something else, the client's Pet Attack key
  (`PETATTACK`, binding grid index 17) is pressed before the opener, then
  again only while the pet is on another unit, at most every 1.5 seconds. No
  facing is needed. The press waits while the fight hands the target back for
  range, so it cannot hold that hand-back off. Upstream's `AutoPetAttack`
  (default true) turns it off; the start-up check refuses a living pet
  without a Pet Attack binding.

The Hunter profile follows the live bar: Arcane Shot moved to slot 4 (Hunter's
Mark is 5 and Concussive Shot 6, neither in the profile). Not done yet: the
pet's health (Mend Pet), Revive Pet and Call Pet, which wait for a pet health
channel and those spells on the main bar.

The add-on and the bot must be reloaded together: the reader refuses roster
version 3. Offline, `--logic-test` has 193 checks (when the pet is sent and
when not, and the start-up check) and `--self-test` covers the pet block and
the version 4 rows at three UI scales. A scratch run of the committed and
working add-on on one mocked API kept all 1,825 other telemetry entries
identical and decoded the pet and the new cells end to end, including a
protected pet target and a failing pet query. Not yet verified live.

### Activities and the agenda (2026-10-04)

Reference: [forever-activities.md](forever-activities.md) (the selector and
the activities), [forever-tasks.md](forever-tasks.md) (the agenda and the
task API), [forever-movement.md](forever-movement.md) (walking, travelling,
roaming) and [forever-routes.md](forever-routes.md) (route files). This
section and the next ones record how they came about.

The controller had grown into one state machine that wired every edge by
hand: Search alone decided about loot, aggro, health and mana, attackers,
looking around and roaming. Adding travel, food, death or quests there would
have kept growing one class. The run is now split the way upstream splits its
goals, keeping the declared state machines inside each part.

- **Facts.** `ForeverFacts` is built once per frame: in combat, in danger,
  taking damage, a unit engaged, bodies pending, enough to open a fight, and
  when the last kill was.
- **Selector.** `ForeverSelector` runs the cheapest activity that wants to
  run, re-checked on every frame, as upstream's `GoapAgent` runs the cheapest
  goal that can. A switch exits the old activity first, so its hooks release
  held keys and the mouse, then enters the new one, and the log says why
  ("Forever activity: Task -> Engage (engaged)").
- **Activities**, each on its own `ForeverStateMachine`:

  | Activity | Wants to run when | Cost |
  |---|---|---|
  | Engage | a unit is chosen to click, or locked to fight | 4 |
  | Loot | bodies are pending, nothing fights the player, `Loot` is on | 4.5 |
  | Threat | in combat with nothing engaged | 4.6 |
  | Rest | out of combat, after a fight until rested, or below what a pull needs | 5 |
  | Task | always: the agenda's current task | 20 |

  Engage is the old Select, Approach and Fight, with the setbacks, the units
  set aside and the recovery budget. Threat is Search's in-combat part: it
  adopts the selected unit that fights in reach, takes an attacking plate,
  stands still while combat only lingers and turns round only while taking
  damage. The costs keep the old precedence: in danger Loot does not want to
  run and Threat goes first; while combat only lingers the bodies are looted;
  an attack during a rest is combat, where Threat takes over.
- **Agenda and tasks.** `ForeverAgenda` holds the run's tasks in order. A task
  is a record (data), carried out by a runner over many frames; it never
  blocks, since a fight can take over on any frame and the task resumes
  afterwards. Kills count toward the task that was current. A task ends when
  its runner says so or its `ForeverUntil` holds, and the run completes when
  the agenda is done. Code builds tasks through `ForeverTasks`, for example
  `ForeverTasks.HuntHere(ForeverUntil.AfterKills(1))`; route files will load
  into the same records.

The bounded trials are agendas of one task: `--auto-run` hunts here until one
kill, `--sweep` until a full look-around finds nothing, and with neither the
hunt runs until its 45-second search bound. Hunting here is Search's
out-of-combat part: look round, take an eligible plate, walk on a stretch
within 25 yards, look again. A verified kill is still checked before anything
else acts on a frame, and the profile's Adhoc actions still run first, except
while looting out of combat or with a corpse selected out of combat.

Behaviour is meant to be unchanged. The differences are small: the look-around
starts afresh when hunting hands over to Threat or back; waiting for the
health or mana a pull needs is Rest, so its time bound says "health did not
recover in time" instead of the search timeout; combat that lingers after a
kill is bounded by Threat's 45 seconds instead of Rest's 30; the Recover phase
is gone and its log line comes from the engagement. The trial runner shows
the activity and step, such as `Engage/Fight`, `Threat` or `Task/Hunt here`.
Offline, `--logic-test` has 207 checks: the old phase checks ported to the
activities, which activity the facts pick in every case above, a loot round
ending itself after its last body, and the trial agendas. Not yet verified
live.

Built on it since: `TravelTo`, `RoamArea` and route files that chain them
(see "Travelling", "Roaming an area" and "Route files" below). Quests come
later as more step kinds and conditions on the same agenda ("Quests (later)").

### Travelling (2026-10-04)

`ForeverTasks.TravelTo(point, via, pulls)` walks to a point on a zone map,
given in the coordinates a player reads (0 to 100, as upstream's recorded
routes store them), through optional points on the way, and is done on
arrival: within 3 yards of the destination, and within 6 of a point on the
way, so the walk flows through it.

- **Where to.** `ForeverMapPoint` converts map percent to world yards through
  the conversion the live preflight checks against the client's own world
  position. A point on a map not verified, on another continent or outside
  the baked mesh is refused when the task starts, with why.
- **The way.** A full path over the baked Durotar mesh costs 0.15 to 2 ms up
  to 400 yards but 20 to 26 ms around 1,000 yards (measured offline), too much
  for every frame. The travel asks for a leg's whole path every 5 seconds, or
  when it strays 8 yards off it, as after a fight or an unstick step, and on
  each frame walks toward the point 20 yards along it with the existing
  held-forward, mouse-steered walk.
- **Stalls.** Upstream's StuckDetector ladder, redone for held keys and the
  mouse. The walk's own hop when held movement stalls comes first. Then, each
  time the walk stalls (no progress with forward held, or no way from here),
  the next step: step aside with a held strafe to whichever side is walkable,
  back out with move-backward held while the camera turns, skip the point on
  the way, then step aside and back out in turn. Each step lasts about a
  second; then the way is asked for afresh. As upstream, only a new closest
  approach counts as progress (a yard closer along the path) and resets the
  ladder; without one for 20 seconds the travel stops with why. Move-backward
  is the client's binding (grid index 3); without one the ladder only steps
  aside.
- **Fights on the way.** Units that attack are always fought (Threat), and
  the travel resumes from wherever the fight left the character.
  `ForeverPulls.Any` also takes eligible units in view, as hunting does,
  checking first that every attack is usable.
- **Hops.** The walk may hop when snagged and now and then for no reason, as
  when hunting; the unstick steps only step and turn.

Trial: `ForeverControllerTrial --travel <x> <y>` walks to that point of the
current zone, within a 5-minute bound; its snapshot lines now show the map
position. Offline, `--logic-test` has 218 checks, among them a walker that
follows a real 190-yard Durotar path from Razor Hill to its end. Not yet
verified live.

### Roaming an area (2026-10-04)

`ForeverTasks.RoamArea(area, until, pulls)` hunts inside an area until its
condition holds: a number of kills, minutes since it began, or a level,
whichever comes first.

- **The area.** `ForeverArea` is a polygon on one zone map in the coordinates
  a player reads, or a rectangle from two opposite corners, converted to world
  yards as travel points are.
- **Spots.** Chosen the way upstream's generated Wander routes pick their
  stops: a seeded random point of the area, snapped onto the ground under it
  (the height comes from the column under the point and the snap stays tight
  around it, so a roof or a canopy is not taken), kept only while inside the
  area and reachable from where the player stands, and at least 25 yards from
  the spot before it (40% of the narrower side in a small area). The seed is
  logged, and a task can pin it.
- **The roam.** From outside it walks in to the nearest reachable spot inside;
  inside it looks round in a calm full circle, walks to the next spot and
  looks round again. Units in view are taken while walking and looking
  (`ForeverPulls.Any`, the default) and fought by Engage; afterwards the roam
  goes on toward the same spot, back into the area if the fight led out of it.
  There is no leash yet: a pursuit may follow a unit outside the area.
- **Walking.** Travel's walking engine is now `ForeverWalker`, shared by both:
  the whole path every few seconds, the point 20 yards along it, the unstick
  ladder and the 20-second rule. For a roam, the ladder's third step gives the
  spot up and draws another; four spots given up in a row, or three draws that
  find no spot, stop the run with why.

Trial: `ForeverControllerTrial --roam <x1> <y1> <x2> <y2> <kills>` roams that
rectangle of the current zone until that many kills, within a 15-minute bound.
Offline, `--logic-test` has 225 checks, among them the picker drawing ten
spots around Razor Hill on the baked mesh, every one inside the area, on the
mesh and reachable from its middle. Not yet verified live.

### Route files (2026-10-04)

A route file is the agenda as data, in `Json/path/_forever`: steps in order
on one zone map, in the coordinates a player reads (0 to 100), each a
`Travel` or a `Roam`. It loads into the same tasks the `ForeverTasks` API
builds (`ForeverRouteFile`), so a route written by hand and one built in code
run the same way. `durotar_example.json` ships as a commented example:

```json
{
  "Name": "Durotar example: south of Razor Hill",
  "UIMapId": 1411,
  "Steps": [
    { "Travel": { "To": [52.5, 44.5] } },
    { "Roam": { "Area": [[51.5, 44.0], [53.5, 46.0]], "Until": { "Kills": 3, "Minutes": 10 } } },
    { "Travel": { "To": [52.5, 43.0] } }
  ],
  "Repeat": false
}
```

- **Travel** walks to `To` through the `Via` points, in order. `"Pulls":
  "Any"` also takes units in view on the way; `"None"`, the default, fights
  only what attacks.
- **Roam** hunts inside `Area` (two opposite corners, or three or more in
  order) until `Until` holds: `Kills`, `Minutes` or `Level`, whichever comes
  first. A roam must say when it ends. `"Pulls": "None"` roams without taking
  units; `Seed` pins the spots it walks between.
- A step may name another map with its own `UIMapId`. `"Repeat": true` starts
  over after the last step, a farming loop that runs until the time bound;
  laps are logged.
- **Mistakes are refused when the file loads, naming the step**: a step with
  both kinds or neither, a point that is not `[x, y]` or off the map, a map not
  verified, a roam without an end, and any field the format does not know, so
  a misspelt `Kils` fails instead of silently doing nothing.
- **Start-up check.** Before anything moves, every travel point must be on
  the player's continent and on the baked mesh, and every roam area must have
  walkable ground inside.

Trial: `ForeverControllerTrial --route <file>` runs a route (20-minute bound,
`--minutes <n>` to change it) and prints each step as it starts with the
agenda's progress; the file is read and checked before anything attaches to
the game. `ForeverVisualReader --where [seconds]` prints the player's zone and
map position, as `[x, y]`, whenever it changes, so a route is written by
walking to each stop or corner and copying it. Offline, `--logic-test` has 229
checks, among them the example loading, every refusal naming its step, a
repeating agenda starting over, and the start-up check finding the example's
points on the baked mesh. Not yet verified live.

### Any zone, and the bar's spell ranks (2026-10-04)

Before the first live test of the activities, the Hunter had levelled to 12
and moved to the Crossroads in The Barrens: outside the two zones the runtime
accepted, and with higher spell ranks on the bar than its profile names. Both
limits went:

- **Any zone of a baked continent.** `ForeverVerifiedSetup` no longer lists
  maps; it keeps the profiles and classes. A zone works when the map data has
  it (`Json/dbc/forever/WorldMapArea.json` holds every zone of both
  continents) and its continent's mesh is baked. The navigator's start-up
  check still proves the client's map position against its world position
  there, and now also that the map data puts the zone on the continent the
  client reports. The mesh is one per continent, so a walk that crosses into
  the next zone keeps it, and a route continues there with a step's own
  `UIMapId`. Another continent needs a boat or a zeppelin, which no task takes
  yet, so its points are refused at start-up. Walking into a zone the map
  data lacks, or onto a continent without a bake, fails the walk with the
  zone named.
- **The bar decides the rank.** A profile names a spell by one rank's ID, and
  the client puts each newly trained rank on the bar. The rotation's start-up
  check (`TakeRankFromBar`) gives an action the ID of the rank on its slot
  when the spell data names the same spell, and logs it once, so the slot
  checks, the cast gate and the cast confirmation expect that rank. Another
  spell on the slot is still refused, naming the action. This is the
  rank-selection rule the early trial note above asked for.

Offline, `--logic-test` has 232 checks, among them a travel point at the
Crossroads passing the start-up check from Razor Hill, an unknown map refused,
and Holy Light taking rank 2 from its slot and back. Not yet verified live.

### Live tests, line of sight and minimap tracking (2026-10-04)

The first live runs of the activities, on the level 12 Hunter around the
Crossroads in The Barrens: the read-only checks, one hunt cycle (`--auto-run`),
the sweep (`--sweep`, two kills), and travel over open ground and over a
402-yard route through hilly ground with an attacker on the way, all passed in
the end. What the runs showed, and what changed:

- **Clear target.** The bot fell back to Escape, which opened the game menu
  whenever the target was already gone; with the menu open the game stops
  turning the camera, so the mouse moved the visible cursor instead.
  ForeverProbe now makes its own `/cleartarget` secure button and binds the
  first free key of Shift+Insert, Shift+F12, Shift+F11, Shift+Home and
  Shift+End to it for the session (`SetOverrideBindingClick`, so saved
  bindings are untouched), and fills DataToColor's clear-target row (58) of the
  binding grid with it when BindPad's is missing. Escape is never pressed, and
  the start-up check needs a clear-target key.
- **The pet goes first.** The pull waits until the mob targets the pet and has
  stayed on it 1.5 s (8 s at most), so the mob stays on the pet and the Hunter
  at range; Pet Attack presses are logged and the snapshot shows the pet.
- **Out of sight.** A refused attack or Auto Shot's "out of line of sight" in
  combat sends the fight to `Sight` (see
  [forever-activities.md](forever-activities.md)). The first version did not
  move: its walk asked for a path ending within 8 yards of the estimated
  position, which a mob below a ledge never had. It now walks as close as the
  mesh comes, steps aside where it comes no closer, and presses again only
  after moving. Live, it saw the mob again after one step aside, twice.
- **Which floor.** The navigator gave the pathfinder the surface nearest
  height 0 (`TryGetHeight`) as the player's height, which bypassed the column
  scan built for Forever's missing Z; on a mountain top the bot then stood on
  the ground below, and no path to a body there existed. Paths are now asked
  for without a height, with the surface nearest height 0 as the second try,
  and a walk along a route passes the route's own heights
  ([forever-movement.md](forever-movement.md), "Which floor"). Live: a body on
  the mountain top looted, and the route that had stalled beside a rock walked.
- **Bodies** are walked to within 5 yards when their estimated place is not
  reachable exactly.
- **Turning** standing is capped at 0.6 of full mouse speed and looking for a
  lost target at 0.35, after a run turned round violently.

**Minimap tracking.** No game function returns other units' positions, but a
Hunter's tracking (Track Beasts, Track Humanoids, ...) draws them on the
minimap, and the minimap is in every capture. ForeverProbe writes a minimap
block on the world row, cells 15 to 20 (frame row 105): the centre across and
down as shares of the screen (×65535), the radius as a share of the width,
`C_Minimap.GetViewRadius()` ×100 (233.33 yards at the widest zoom), the flags
(bit 0 `rotateMinimap`, version 1 from bit 4), and a checksum from 0xA5F122.
`VisualMinimapReader` decodes it, and the tracker scans only the minimap's
square of the frame: every dot has a gold ring, its centre gold for a neutral
unit, dark red for a hostile one and green for a friendly one; gold and red
count, green does not, and the outer band beyond 0.78 of the radius, where the
minimap draws its arrows and icons, is left out. Each dot becomes a world point
(`VisualBarSample.TrackedBlips`). Tracking only adds to the nameplates: a
sighting takes a dot when one lies along its bearing (within 6 degrees, or 4
yards across) and as far as the plate allows (±4 yards; the target's marker
out to 100 yards), and then `TryLocate` and a body's place are that point. With
no dot, or no tracking, nothing changes. Calibrated on a live capture: 10 of
the 10 tracked dots found, the green one left out; not yet used in a live
fight. `ForeverVisualReader --watch-tracking [seconds]` prints the dots, in
yards and in map coordinates, and the one along the target's bearing;
`--read <png>` does the same for a saved capture.

Offline, `--logic-test` has 244 checks.

### Fighting back, and choosing pulls by cost (2026-10-04)

The first roam test (`--roam`, west of the Crossroads) pulled a mob, and a
second, hostile mob joined on the pet. The first died; the second killed the
pet and went for the player, and the bot never attacked it: for 30 seconds
Threat stood still, then turned round and round while taking damage, until the
run was stopped. The second mob was almost certainly above the profile's level
window (`NPCMaxLevels_Above` 1), and the window applied to attackers too, in
three places: which plates count as eligible (so `TryChoose` passed it over
and `AttackedByAnother` ignored it, both by design and with a check saying
so), the selected unit's eligibility, and `ForeverCastGate`'s level cap,
which refuses every press on such a target. What changed:

- **Fight back whatever the level.** A unit that fights the player or the pet
  (`VisualNameplate.Attacks`) only has to be attackable, alive and not a
  player (`ForeverTargetPolicy.IsDefendable`); the level window, `TargetMask`
  and the tap decide only what the run pulls. The selected unit follows its
  own plate (`TargetAttacks`), and the cast gate's cap applies only while the
  target does not fight back. The user's rule: "if 2 mobs get pulled u should
  kill them both".
- **No spinning while an attacker is known.** In combat Threat takes the
  attacker it can click (`TryChooseAttacker`); an attacker whose plate shows no
  marker, as one on the pet behind the player, is turned to at once, toward
  where it was last seen (`ForeverNavigator.TurnRateToward`), whether or not
  it hits the player. The look-around while taking damage remains for
  attackers no plate shows.
- **Pulls by cost.** Out of combat `ForeverPullOptions` lists every unit the
  run could pull and orders them: first the fewest hostile units within 20
  yards that could join the fight, then the least effort (its distance, 40
  when the camera cannot see it, 10 out of reach, 5 per level above, 5 for a
  click); each pull logs its options. The user asked for this: "aim for mobs
  that are alone ... if a yellow mob is near a red, u go for the red first
  then the yellow ... if 2 reds are together u try to pull only 1", as "a
  class/method that ... resolves what is the most beneficial based on some
  cost analysis ... present with the options". A first version added the
  parts into one cost and refused pulls above a ceiling; the user saw the
  flaw ("even if i want to get to a mob i avoid getting there in the first
  place cause the cost model tells me its not worth it"): distance and level
  then decided whether a mob was taken at all, from estimates that are rough
  far off. Now the order never refuses: what may be pulled is the profile's
  and the target policy's to say, and a lone mob a level or two above is
  pulled however far ("if a mob is a level above me or 2 but theres nothing
  else around that doesnt mean i shouldnt fight it"); the Hunter profile's
  `NPCMaxLevels_Above` is 2 for that. Details in
  [forever-tasks.md](forever-tasks.md).
- **A pull on its own.** When another hostile unit stands near the mob, the
  Hunter keeps the pet back: the opener draws the mob to the player, and the
  pet goes in once the mob fights.

Units are placed as the bodies are: the marker's bearing and the range flags'
band, or the minimap's tracked dot along the bearing. Not yet used: tracked
dots without a plate (a hostile unit beyond plate distance or behind the
camera); that needs the dot's colour kept with its position. Nor is the way
to the mob weighed, such as hostile units the pet would pass on its run in.

Offline, `--logic-test` has 263 checks.

Live, the second roam test (2026-10-04, west of the Crossroads) killed two
hostile mobs, the second one joining the first and fought right after the
first kill. Then it walked between random spots for two minutes with no
nameplate in range while the minimap showed tracked mobs farther out, and
the user stopped it. Their rule for "wander mode": what the camera shows is
killed first, until nothing is in view; then go where the minimap's tracking
shows mobs; with neither, go to the part of the area covered least; and "this
should be in the context of a API. there should be methods that resolve
that". Built as `ForeverPatrol` (`TryRemembered`, `TryTracked`,
`TryLeastCovered`, `TryNext` in that order) with `ForeverMobMemory`, which
remembers every plate the camera showed so any class can go back to a mob out
of view; see [forever-movement.md](forever-movement.md), "Where the patrol
leads". The roam now looks round after each kill.

The same run had four failed selections at its start (two "the unit selected
is not the eligible candidate", two "the candidate's nameplate is gone"), new
in this run. The log could not say which check refused the unit: the
selection now waits 600 ms for the target's rows to agree with the plate
that confirmed it, and a failure names the check, the target's identity and
the candidate's plate (level, reaction, where its marker was).

The third roam run (north-west Barrens) stopped after 26 seconds with two
failed selections, and the new reasons named them: both candidates' markers
lay at the right edge of the screen, one on the minimap (0.91, 0.89) and one
at the edge (0.99, 0.84). The look round turned them into view at the edge
and the run went for them at once; the minimap took the click, and the edge
clipped the marker out of sight. A person would turn to face the mob first,
and the run now does: a click goes only to a marker in the middle of the
screen, clear of the edges and the default interface
(`ForeverTargetPolicy.Clickable`), and when the best pull stands at the side
the run stops, turns to face it and then clicks (`ForeverPull.Turning`). In
combat an attacker beside the middle is turned to, toward where it was last
seen, rather than clicked, and one low on screen between the clickable sides
is still taken when no other is.

Offline, `--logic-test` has 272 checks.

**A trip with everything on it.** The user asked for one scenario that uses
the API end to end: travel to the Crossroads, clearing the mobs on the way,
looting and skinning each body, as an optional profile flag. Built for it:
`ForeverPulls.OnTheWay` (a travel takes only units within 20 yards of the
path still to walk, over its next 80; `ForeverWalker.IsOnTheWay`, passed to
`ForeverPulling.TryPull` as a filter on each option's place, which
`ForeverPullOption.At` now carries), and skinning in `ForeverLoot` behind the
profile's `"Skin"` (upstream's flag): the Interact Key again on the emptied
body, the Skinning cast and its window, or the body left when the game does
not allow it. The route is `Json/path/_forever/crossroads_trip.json`; the
Hunter profile has `"Skin": true`. Handing in a quest at the end has no code
yet (quests come later, see below) and is left to the user.

Offline, `--logic-test` has 276 checks.

Live, the trip (`5-crossroads`) walked toward the Crossroads and pulled
three lone neutral mobs on its way, each by the cost order, and walked on
after each fight, until the user stopped it. It looted none: both bodies it
looked for were melee kills, whose line runs from where the player stood to
8 yards ahead, and the round reached the line's end in about two seconds and
gave the body up, the Interact Key's soft target never having shown it (in
the passing run 2a the body was found 11 yards off). The round now turns
round once at the end of the line before giving a body up, and a body of the
fight that the key finds with nothing on it is done, or skinned, instead of
walked past.

Offline, `--logic-test` has 277 checks.

The second trip (`5-crossroads2`) got to the Crossroads with two kills on
the way, and skinned the first body, but looted neither. The Interact Key
found both bodies, and `CanLootUnit` read false for both, which a first
version took for "nothing on it": it went straight to skinning. On the first
body the press opened its loot window (one item, which closed untaken), on
the second nothing came of it. `CanLootUnit` reads false out of reach too, as
the looting notes of 2026-10-04 above found. The round now trusts the key:
it presses on a body of the fight the key finds, steps closer on "too far
away", loots first and skins after; see [forever-activities.md](forever-activities.md).

Offline, `--logic-test` has 280 checks.

The third trip, from Ratchet after a client restart, walked 75 yards and then
stood on one spot for 20 seconds until the walk gave up: no unstick step ran.
The path was complete (1,336 yards to the Crossroads); the step's own
3-yard look-ahead failed. `TryWalkStraight` refuses a start more than half a
yard from the walkable surface, which the mesh keeps a character's width from
walls and objects, and the navigator answered a failed look-ahead with a
standing turn that never ended. The look-ahead is now judged from the nearest
walkable point within 2 yards, and facing the way with no walkable way ahead
reports `Blocked`, so the walker's unstick ladder runs
([forever-movement.md](forever-movement.md), "Getting unstuck").

The fourth trip walked out of Ratchet and pulled a neutral level 16 mob; the
pet held it to about 29%, then the fight stalled in the hunter's dead zone,
too close for the bow and not in melee reach, the pet died, and the player
fell to 23% while the bot moved about 80 yards with the mob behind it: the
approach's local search walked forward looking for a target that stood at
its back. The user stopped it and gave the rule: prefer range while the mob is
not on you, melee only when it is or without ammo, back to range when it
leaves you. Built as the fight's stance (`ForeverStance`, `ForeverRotation.Stance`,
the `StepBack` step and `ForeverNavigator.StepBack`); a close target out of
view is turned to, never searched for on foot. See
[forever-activities.md](forever-activities.md), "Ranged or melee: the stance".

Offline, `--logic-test` has 288 checks.

The fifth trip stopped itself in its first fight: an Arcane Shot was pressed,
the target vanished before the success event came, and the caster's wait ran
out, which stops the run ("Arcane Shot success was not confirmed"). A press
whose target is gone had nothing to land on: `ForeverCaster` now gives such a
press half a second for a late success or death event, then returns
`ForeverCastResult.Voided`, and the fight's own handling of a vanished target
goes on. The same trip showed the first walking fix stalling too eagerly
(sidesteps where a little more turning would do); it now stalls only after 2
seconds of turning standing.

### Falling back instead of stopping (2026-10-04)

The sixth trip (`5-crossroads7`) pulled two lone neutral mobs on its way and
killed both. The first body was skinned. The Interact Key found the second
body from where the player stopped, about 11 yards on, reading not lootable;
two presses there opened no window and started no cast, and the round gave it
up. Then, walking on, the run stopped: "visual controller frame unavailable
for five continuous seconds". The game kept focus throughout and the log could
not say why the frame went (the reader kept no reason).

The reasons the reader now keeps, and the capture the trial saved, showed it
the same evening: the reader anchors on the add-on's full bar, the longest run
of white in the top-left corner, and with the camera raised toward a bright
daylight sky the sky behind that corner was a longer white run (550 pixels
against the bar's 320), or merged with the bar's own row. Looking at the ground
read fine. The anchor bar is now magenta, which the sky never is, a white or
magenta run counts only with the half-filled yellow calibration row above it,
and the add-on draws black behind the five bars, so the scene never touches
them. The self-test covers a bright band above the bars and haze beside the
anchor row. An add-on from before this change no longer reads: `/reload`
after copying it.

Every earlier run that looted (2a, 2b, the mountain, 3b, the second roam)
pressed only on a body that read lootable, in reach. Both presses that came
to nothing (here and in `5-crossroads2`) came the moment the key first found a
body that did not. The rule "trust the key" stays, from closer: a body reading
lootable is pressed at once; one that does not gets a step closer first, and a
press nothing answers, or that draws "too far away", another step and press,
3 presses in all. Only the game's word leaves a body sooner: full bags (every
body then, the user's one reason), not the player's to loot, or empty and
beyond the skinning skill. ForeverProbe's error latch now codes those messages
and the loot and interact "too far" ones. See
[forever-activities.md](forever-activities.md), "Loot".

The user also asked that a failure never leave the bot with nothing to do,
falling back to the bigger goal each time, up a hierarchy of goals. The layers
were there (a press, a body or a unit, an activity, a task's places, the
task, the route); what was missing was that every failure stopped the run.
Now each layer absorbs the failures of the one below
([forever-activities.md](forever-activities.md), "When something fails"):
activities fall back as each knows how and start afresh, a failed task is
given up for the next, a travel walks to its destination again before it
fails, and the last task of a route is retried after hunting here a while.
The run stops only when the game loses focus, the setup breaks, the player
dies or the code throws. Without a readable frame it pauses, says why, and
waits (the trial runner saves the first unreadable capture in `local/logs`).
No generic goal tree was built: the user asked whether this was getting too
complicated, and the existing layers are the tree.

The next trip (`5-crossroads11`) read every frame with the magenta anchor and
looted its first body on the second press, a step closer. In that first
fight an Arcane Shot's success never showed (only Auto Shot events within
3 seconds, the target unchanged), which closes casting for good by design; the
fight fell back, but every press of the next fight was refused at once, and
a first version stopped the run after ten such failures in a row. The user:
"we got the fallbacks it should never terminate unless its on the goal". Every
fallback now opens casting again, and every tenth failure in a row gives the
current task up instead of stopping.

The trip after that (`5-crossroads12`) passed: it arrived at the Crossroads in
3.5 minutes with 8 kills, every frame read, all 8 bodies looted (6 on the
second press, a step closer) and both beasts skinned. Twice an Arcane Shot's
success did not show; each time the fight fell back once and went on to a
verified kill. Open from it: the Arcane Shot event is likely overwritten by an
Auto Shot one in the add-on's single cast latch (only Auto Shot events seen,
three in the 3 seconds); and six windows "closed with 1 items left", every
body that was not a beast, which looks like the coin slot not reporting
`LOOT_SLOT_CLEARED` rather than an item left behind.

Offline, `--logic-test` has 302 checks.

### Quests (later)

Not designed yet; this note records what the design above leaves room for.
While levelling, quests should work together with grinding on one route: walk
to coordinates, speak to an NPC to take or hand in a quest, roam an area until
the quest's objectives are done, and grind on. In this design a quest needs
no new machinery, only:

- **More step kinds**: talk to an NPC at given coordinates to accept or hand
  in a quest, interact with an object. Each is a task with its runner, like
  `Travel` and `Roam`, built from code and from route files alike.
- **More `Until` conditions**: a quest's objectives done, an item count. These
  need quest-log and bag telemetry from the add-on.
- **More facts**: an NPC id per nameplate, so a roam can take only the mobs a
  quest asks for ("kill 8 of this mob").
- **Interaction without chat commands**: the NPC clicked as a person does
  (`ForeverPointAndClick`), and the quest frames answered the same way.

A levelling route then interleaves quest steps with `Roam` steps; the
reactive activities (fight, loot, rest) still take over and hand back as now.
