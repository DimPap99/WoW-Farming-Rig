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
   visual data required by the Forever dashboard and controller trial. Its
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
   marker; the marker stays black if any position source fails. Two more squares
   indicate cooldown validity and target presence (green = present, red = absent,
   black = unknown). Two additional squares carry the watched action's numeric
   ID and a validity/type/slot marker; missing or mismatched pixels are rejected.
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
   A separate cyan sixth bar shows the remaining cooldown
   fraction for action-bar slot 1, or
   the slot selected with `/forevercooldown 1` through `/forevercooldown 12`.
   This cooldown bar is still experimental.

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
five bars, plus the watched action's cooldown fill when its marker is present.
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
  composition root), `ForeverCaster` (one checked keypress per cast),
  `ForeverRotation` (the class profile's Pull, Combat and Adhoc lists),
  `ForeverLoot`, `ForeverNavigator`, `ForeverTargetSelector`,
  `ForeverReaderFeed` (feeds each capture tick to the shared readers),
  `ForeverFrame` (which inputs a frame must carry and the resource levels),
  `ForeverBinding(s)` (the bindings checked and pressed), `ForeverNavmesh`,
  `ForeverVerifiedSetup` (the verified profiles, map and classes) and
  `ForeverProfileRules` (what a Forever profile may use).
- `Core/Goals/ForeverAutonomousGoal.cs`, `ForeverTrialGoal.cs`,
  `Core/GoalsComponent/ForeverCastGate.cs`, `Core/GOAP/ForeverGoapWorldState.cs`,
  `Core/ForeverDiagnosticController.cs`, `Core/Session/NoGrindSessionHandler.cs`.
- `Core/WoWScreen/ForeverVisualBarTracker.cs` and the `Visual*Reader` decoders.
- `Game/Input/MouseCameraInput.cs`, `Addons/ForeverProbe`, `Utilities/Forever*`.

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
- Frontend: the Forever status page and navigation, and the hidden `BotHeader`.

A Forever-owned controller loop would let `BotController`, `GoapAgent`,
`PlayerReader`, `KeyReader` and `RequirementFactory` return to upstream as well.

### Class-agnostic rotation (2026-10-03)

The runtime names no class or spell. As upstream, the class profile carries
everything class-specific and the behaviour is shared:

- `ForeverAutonomousGoal` runs Acquire, Pursue, Fight, Loot and Recover for
  every class.
- `ForeverRotation` reads the profile. **Adhoc** holds self heals and buffs,
  gated by the upstream `InCombat` flag. A buff is an action with
  `AfterCastAuraExpected`, and its aura is matched against its slot icon
  (`VisualBarSample.SlotAuras`). **Pull** holds openers, each used once per
  target and only while every buff is up. **Combat** is the priority list, and
  `AutoAttack` presses `STARTATTACK` once per target. Pursuit stops when any
  opener can be pressed or any Combat spell's slot reports the target in range.
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
- A profile `MacroText` is typed as a command, as upstream does. This is how
  `[@player]` self casts are expressed.

`Paladin_Forever_Auto.json` encodes the thresholds the Paladin code used to
hard-wire: Holy Light below 70% in combat and below 90% out of combat, Seal at
70% health and 25% mana, Judgement as the opener, then melee and Holy Strike. A
new class needs a profile, a live bounded sweep, and its class ID in
`ForeverVerifiedSetup.IsVerifiedClass`. Rogue finishers, DoTs, forms and pets
still need channel work first; the profile rules refuse them.

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
in `ForeverRunOptions`. It acquires with the checked Tab binding, locks that
opponent through pursuit and combat, holds forward while steering with the right
mouse button, and uses checked spell range to stop. Seal (slot 5), Judgement
(slot 2), melee start and Holy Strike (slot 3) are enabled. `Loot` is now true.
This profile remains available through the explicit bounded utility;
`ForeverDiagnosticController` still guards dashboard startup.

Read-only current-position preflight:

```powershell
dotnet run --project Utilities/ForeverControllerTrial/ForeverControllerTrial.csproj -- --auto-check
# One complete kill / loot / recovery cycle:
dotnet run --project Utilities/ForeverControllerTrial/ForeverControllerTrial.csproj -- --auto-run
# Repeat cycles until Tab has no eligible target; no roaming:
dotnet run --project Utilities/ForeverControllerTrial/ForeverControllerTrial.csproj -- --sweep
```

Tests start at the current player position, without a fixed route origin. The
single-cycle utility has a 170-second total bound; the sweep has a 600-second
bound and stops after two unanswered Tab attempts, or after revisiting a rejected
candidate. It uses the game's Tab targeting selection; it does not measure an
exact 90-degree enemy cone. Each pursuit remains bounded to 35 yards / 20 seconds.
The controller reports normal completion separately from failures.

The first attempts exposed several defects now fixed: marker detection had been
cropped to the left 650 pixels; mouse focus changes could cancel a forward key
pressed beforehand; combat entry could trigger another Tab; and a target could
clear before its dead flag reached the next screenshot. Capturing the entire
viewport, taking focus before key-down, retaining the acquired identity, and
latching kill events address those cases. Target clearing now uses `/cleartarget`
instead of Escape, which could open the game menu on a repeated call.

Local movement uses a Detour raycast through connected polygons, returning the
height at the endpoint. It no longer rejects ordinary slopes solely for a
1.5-yard height change or independently resolves the endpoint onto another floor.
With `NavmeshQueryOptions.PreferGroundUnderPoint`, which only the Forever
navmesh turns on, the column resolver prefers ground under the requested X/Y
over a higher surface beside it; Classic clients keep the upstream behaviour. Blocked straight movement tries checked sidesteps and pathfinder
waypoints while retaining the same target.

Live target-position audit on a selected level-6 mob found both
`UnitPosition("target")` and `C_Map.GetPlayerMapPosition(mapId, "target")`
unavailable, while the corresponding player positions work. A one-press
`INTERACTTARGET` approach did not move the character. `ClosestUnitPosition`
returned no usable result for the selected creature, and
`UnitDistanceSquared("target")` returned zero for a visibly distant mob, so
the latter is rejected. A normal nameplate frame list did contain the selected
mob: `C_NamePlate.GetNamePlates()` plus `frame:GetUnit()` and `UnitIsUnit`
matched it, though `frame:GetCenter()` gave no usable numeric point.

The add-on anchors a magenta/cyan visual marker with a black interior to that selected
nameplate. A saved live 1920x1080 capture decoded its position as normalized
screen X 0.548, Y 0.915. The reader matches the marker only when the same
capture contains a checked target identity and a matched nameplate parent;
its three-scale synthetic test passes, including a marker beyond X=650. Screen
guidance remains conditional on nameplate visibility. The navigator uses marker
X for mouse steering, Judgement range for the initial cast, then Holy Strike
range for melee. This passed the live controller cycle below. The external
`--watch-target-marker` overlay displays screenshot-derived X/Y; the add-on cannot
recover those coordinates from its protected nameplate frame API.

### Death and loot events

`ForeverEvents.lua` observes `PLAYER_TARGET_DIED` and `PARTY_KILL`, deduplicates
them by GUID, and latches a sequence plus the victim's two identity hashes. A
matching new event proves death even if the current target is already empty.
The live record identified `PARTY_KILL` as the source. `LOOT_OPENED`,
`LOOT_SLOT_CLEARED`, and `LOOT_CLOSED` similarly persist across captures.

`IForeverLootRoutine` restores the corpse when necessary, checks its identity,
presses the bound `INTERACTTARGET`, and uses `C_LootFrame.TryAutoLoot()` if the
window remains open. It requires all tracked slots to clear and the window to
close; a timeout or partial collection is reported. It changes no auto-loot CVar.
An explicitly non-lootable corpse completes without claiming items were collected.

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
