# WoW: Forever port status

This is a work-in-progress port for the **Forever beta 1.60.1.70009** client
(`WowB.exe`, interface `16001`). The dashboard now detects this process and
classifies it as `forever`; the matching `DataToColor_Camelot.toc` loads in the
client. **It does not run the bot yet.** `DataToColor:OnInitialize` intentionally
stops before creating pixel frames or changing CVars on Forever.

## Reproduce the current setup

1. Install the .NET 10 SDK and initialize the `external/DotRecast` submodule.
2. Build with `dotnet build BlazorServer/BlazorServer.csproj -c Release`.
3. Copy `Addons/DataToColor` to the Forever beta client's
   `_classic_beta_/Interface/AddOns/DataToColor` directory. The client must
   select `DataToColor_Camelot.toc`. Restart the game after copying add-on files.
4. The separately installed `Utilities/ForeverProbe` add-on is optional. Its
   `/foreverprobe` command records API value types in `ForeverProbeResults`
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
   A separate cyan sixth bar shows the remaining cooldown
   fraction for action-bar slot 1, or
   the slot selected with `/forevercooldown 1` through `/forevercooldown 12`.
   This cooldown bar is still experimental.

The read-only visual diagnostic can capture a lossless PNG, decode an existing
PNG, or monitor those bars for a bounded time:

```powershell
dotnet run --project Utilities/ForeverVisualReader -c Release -- --self-test
dotnet run --project Utilities/ForeverVisualReader -c Release -- --reader-smoke
dotnet run --project Utilities/ForeverVisualReader -c Release -- --monitor 45
dotnet run --project Utilities/ForeverVisualReader -c Release -- --read path\to\capture.png
```

If multiple `WowB.exe` processes are open, add `--pid <process-id>` to the
monitor/capture command to select the intended window. The reader does not
construct `BotController` or send keyboard/mouse input.

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
```

An optional fourth argument selects the product (default `wow_classic_beta`);
an optional fifth argument selects the local cache directory. This source can
be injected into `MPQTriangleSupplier`, but the Forever WMO placement gate still
prevents a complete navmesh bake.

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
  whole visual sample is missing/stale. This is **not** a navigation system.
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
  health/power getters remain unsupported and explicitly reject Forever.
  The Core-backed live monitor tracked changing bars during another fight and
  returned no sample after `/forevervisual` hid them.
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
  entries. `PlayerReader.TryGetForeverWatchedAction` exposes that result only
  while the visual sample is fresh; the live reader smoke check confirmed it
  stops returning an action after capture expires. This first channel covers
  only the selected slot; it is not a gameplay-ready spell reader.
- A 120-slot read-only action-bar grid and decoder are now implemented. Its
  synthetic tests verify the complete inventory, empty slots, flyouts,
  unreadable entries, and missing markers at three UI scales. The diagnostic
  dashboard shows occupied/readable counts, and `PlayerReader` only exposes the
  grid while the capture is fresh. A live WGC capture decoded all 120 slots as
  readable and nine occupied: slots 1-4, 11, 26-27, and 38-39. Their kinds
  and IDs matched the earlier `/forevercatalog` SavedVariables snapshot exactly.
  A live `PlayerReader` smoke check confirmed that the grid is rejected after
  the visual sample expires. This is metadata only: bindings, usability,
  range, and cooldowns for every slot are still not ported.
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
  A new strict, metadata-only WMO reader decoded its 290 full-detail groups,
  four LOD sets, 6,752 `MODI` model IDs, and 6,106 `MODD` doodad placements.
  It validates that full-detail group IDs and doodad indices exist; synthetic
  malformed-reference checks pass. The numeric group loader also parses
  modern `MPY2` polygon flags (16-bit flags plus a separate 16-bit material
  ID), which replace the old `MOPY` records. A scan of all 290 full-detail
  groups in the large WMO yielded 992,821 raw triangles with indices in range;
  a smaller one-group WMO yielded 1,752. These are raw faces, not a validated
  walkable/collision-only navmesh. Three other roots on the same tile still use
  legacy `MODN` doodad names despite being referenced by numeric MODF IDs.
  WMO root/doodad placement loading remains explicitly unsupported, so no
  Forever navmesh has been baked.

## Required work before attempting gameplay automation

1. Finish the vital-data migration beyond the three percentage getters:
   absolute health/power, mana when it is not the active power type, pet/focus
   vitals, and consumers that need more than percentages are still unsupported.
   The existing 24-bit pixel protocol cannot encode protected health/power
   as-is. Keep the Forever startup guard until missing-data behavior is handled
   across the full gameplay reader.
2. Finish spellbook/action/bag adapter integration and design secret-safe aura
   visual channels. A cyan action-cooldown timer bar has passed one live
   active-cooldown capture, but still needs multi-spell design, missing-data
   handling, and gameplay integration. The existing numeric aura/cooldown
   encoder is not viable under combat lockdown. The current `GetSpellInfo`
   and spellbook shims help package loading but are not a full data reader.
3. Import Forever-specific world geometry and build a matching navmesh. The
   geometry readers now accept a container-neutral `IGameFileSource`, and the
   supplier can be given one directly. `Utilities/ForeverCasc` provides a
   local-only CASC source, but the bake host does not select it yet. Modern
   ADT object placements use FileDataIDs. M2 loading has passed one real
   collision-model probe. Numeric WMO group and doodad references now decode,
   and full-detail group files parse with `MPY2`, but mixed modern/legacy root
   handling, instance transforms and doodad collision remain before baking.
   The storage
   refactor builds and passes the navmesh coordinate/cost-zone/listfile tests,
   but its six-tile MPQ byte-identity bake could not run here because this
   workspace has no `Json/MPQ` archive corpus. Do not reuse the
   pre-Cataclysm navmesh: Forever's world is different.
4. Complete and validate the remaining DBC-derived tables, game profiles,
   data-reader integration, and an end-to-end manual test before removing the
   startup guard. No autonomous movement/combat has been enabled for Forever.

For API signatures, use Blizzard's generated UI documentation from the
[`1.60.1 (70009)` UI-source snapshot](https://github.com/Gethe/wow-ui-source/tree/bd2470aed543f72697a044e989285b6c83e63f73/Interface/AddOns/Blizzard_APIDocumentationGenerated)
and validate behavior in the actual beta client; the client may protect a
value even when its documented return type is `number`.
