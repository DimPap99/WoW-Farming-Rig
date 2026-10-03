-- One-shot compatibility diagnostics: /foreverprobe records which APIs return
-- readable, secret or missing values, and /forevercatalog saves the numeric
-- spellbook and action-slot layout. Neither controls gameplay. The live
-- visual channel the bot reads is in ForeverVisual.lua.
local probe = CreateFrame("Frame")
local bars = {}
-- ForeverEvents.lua loads first (see the TOC) and owns the shared helpers.
local events = ForeverProbeEvents
local isSecret = events.IsSecret
local cooldownTimer = CreateFrame("StatusBar")
local cooldownRadial = CreateFrame("Cooldown")

-- These stay hidden. They only check whether the client permits an opaque
-- cooldown duration to flow from C_Spell into native display widgets.
local function cooldownDisplayStatus(widget, method)
    if type(C_Spell) ~= "table" or
        type(C_Spell.GetSpellCooldownDuration) ~= "function" or
        type(widget[method]) ~= "function" then
        return "missing"
    end

    local ok = pcall(function()
        local duration = C_Spell.GetSpellCooldownDuration(6603, true)
        widget[method](widget, duration)
    end)
    return ok and "accepted" or "error"
end

local function valueStatus(value)
    if value == nil then
        return "nil"
    end
    if isSecret(value) then
        return "secret"
    end
    return type(value)
end

local function status(fn, ...)
    if type(fn) ~= "function" then
        return "missing"
    end

    local ok, value = pcall(fn, ...)
    if not ok then
        return "error"
    end
    return valueStatus(value)
end

-- Test a native display sink without ever comparing, converting, or encoding
-- the protected values in Lua. No bar is shown to the player by this probe.
local function statusBarStatus(unit, resource)
    local key = unit .. resource
    local bar = bars[key]
    if not bar then
        bar = CreateFrame("StatusBar")
        bars[key] = bar
    end

    local ok = pcall(function()
        if resource == "health" then
            bar:SetMinMaxValues(0, UnitHealthMax(unit))
            bar:SetValue(UnitHealth(unit))
        else
            bar:SetMinMaxValues(0, UnitPowerMax(unit))
            bar:SetValue(UnitPower(unit))
        end
    end)
    return ok and "accepted" or "error"
end

-- A readable boolean or number as itself, anything else as its status. Only
-- values that cannot identify a person are kept this way; names and GUIDs
-- go through status() and are recorded as types.
local function probeValue(fn, ...)
    if type(fn) ~= "function" then
        return "missing"
    end

    local ok, value = pcall(fn, ...)
    if not ok then
        return "error"
    end
    if value == nil or isSecret(value) then
        return valueStatus(value)
    end
    local kind = type(value)
    if kind == "boolean" or kind == "number" then
        return value
    end
    return kind
end

-- The unit token a nameplate frame shows, or nil when it is unreadable.
local function plateUnit(plate)
    local ok, unit = pcall(function()
        if type(plate.GetUnit) == "function" then
            return plate:GetUnit()
        end
        return plate.namePlateUnitToken
    end)
    if ok and not isSecret(unit) and type(unit) == "string" then
        return unit
    end
end

-- Main-bar slots holding a spell: their range per nameplate is what the
-- planned roster would carry.
local function mainBarSpellSlots()
    local slots = {}
    for slot = 1, 12 do
        local ok, kind, id = pcall(GetActionInfo, slot)
        if ok and not isSecret(kind) and not isSecret(id) and
            kind == "spell" and type(id) == "number" then
            slots[#slots + 1] = { slot = slot, spellId = id }
        end
    end
    return slots
end

local plateCVars = {
    "nameplateShowEnemies", "nameplateShowAll", "nameplateShowFriends",
    "nameplateShowFriendlyPlayers", "nameplateMaxDistance", "nameplateMotion",
    "nameplateOccludedAlphaMult", "SoftTargetEnemy", "SoftTargetFriend",
    "SoftTargetInteract", "deselectOnClick", "autoInteract", "cameraFov",
    -- A plate's alpha and scale may follow distance or occlusion.
    "nameplateMinAlpha", "nameplateMaxAlpha", "nameplateMinAlphaDistance",
    "nameplateMaxAlphaDistance", "nameplateSelectedAlpha",
    "nameplateNotSelectedAlpha", "nameplateMinScale", "nameplateMaxScale",
    "nameplateMinScaleDistance", "nameplateMaxScaleDistance",
    "nameplateSelectedScale", "nameplateTargetBehindMaxDistance",
}
local getCVar = C_CVar and C_CVar.GetCVar or GetCVar
local getCVarDefault = C_CVar and C_CVar.GetCVarDefault

-- A setting's value; settings are read, never changed.
local function cvarValue(getter, name)
    if type(getter) ~= "function" then
        return "missing"
    end
    local ok, value = pcall(getter, name)
    if not ok then
        return "error"
    end
    if value == nil or isSecret(value) then
        return valueStatus(value)
    end
    return value
end

local function probePlate(plate, unit, spellSlots, anchor)
    local entry = {
        token = unit,
        guid = status(UnitGUID, unit),
        name = status(UnitName, unit),
        creatureId = probeValue(UnitCreatureID, unit),
        level = probeValue(UnitLevel, unit),
        canAttack = probeValue(UnitCanAttack, "player", unit),
        dead = probeValue(UnitIsDead, unit),
        isPlayer = probeValue(UnitIsPlayer, unit),
        isFriend = probeValue(UnitIsFriend, "player", unit),
        reaction = probeValue(UnitReaction, unit, "player"),
        tapDenied = probeValue(UnitIsTapDenied, unit),
        inCombat = probeValue(UnitAffectingCombat, unit),
        threat = probeValue(UnitThreatSituation, "player", unit),
        isTarget = probeValue(UnitIsUnit, unit, "target"),
        isMouseover = probeValue(UnitIsUnit, unit, "mouseover"),
        targetsPlayer = probeValue(UnitIsUnit, unit .. "target", "player"),
        behindCamera = probeValue(C_NamePlateManager and
            C_NamePlateManager.IsNamePlateUnitBehindCamera, unit),
        position = status(UnitPosition, unit),
        distanceSquared = status(UnitDistanceSquared, unit),
        interactDistance = probeValue(CheckInteractDistance, unit, 3),
        center = status(function() return plate:GetCenter() end),
        alpha = probeValue(function() return plate:GetAlpha() end),
        effectiveAlpha = probeValue(function() return plate:GetEffectiveAlpha() end),
        scale = probeValue(function() return plate:GetScale() end),
        effectiveScale = probeValue(function() return plate:GetEffectiveScale() end),
        anchor = pcall(function()
            anchor:ClearAllPoints()
            anchor:SetPoint("CENTER", plate, "CENTER")
        end) and "accepted" or "error",
        actionRange = {},
        spellRange = {},
    }
    pcall(anchor.ClearAllPoints, anchor)
    for _, spell in ipairs(spellSlots) do
        entry.actionRange[spell.slot] = probeValue(C_ActionBar and
            C_ActionBar.IsActionInRange, spell.slot, unit)
        entry.spellRange[spell.slot] = probeValue(C_Spell and
            C_Spell.IsSpellInRange, spell.spellId, unit)
    end
    return entry
end

-- What the client exposes about units with a nameplate without selecting
-- them, and the settings that decide which plates exist. The hidden anchor
-- frame only tests whether a marker could follow a plate.
local plateAnchor
local function nameplateProbe()
    plateAnchor = plateAnchor or CreateFrame("Frame", nil, UIParent)
    plateAnchor:Hide()

    local result = { cvars = {}, cvarDefaults = {}, units = {}, plates = {} }
    for _, name in ipairs(plateCVars) do
        result.cvars[name] = cvarValue(getCVar, name)
        result.cvarDefaults[name] = cvarValue(getCVarDefault, name)
    end
    for _, unit in ipairs({ "target", "mouseover", "softenemy", "softfriend",
        "softinteract" }) do
        result.units[unit] = {
            exists = probeValue(UnitExists, unit),
            guid = status(UnitGUID, unit),
        }
    end
    result.minimapRadius = probeValue(C_Minimap and C_Minimap.GetViewRadius)
    result.fovDefault = probeValue(GetCameraFOVDefaults)

    local ok, plates = pcall(function() return C_NamePlate.GetNamePlates() end)
    if not ok or isSecret(plates) or type(plates) ~= "table" then
        result.count = ok and valueStatus(plates) or "error"
        return result
    end
    result.count = #plates
    local spellSlots = mainBarSpellSlots()
    for index = 1, math.min(#plates, 6) do
        local plate = plates[index]
        local unit = plateUnit(plate)
        result.plates[index] = unit and
            probePlate(plate, unit, spellSlots, plateAnchor) or "unreadable unit token"
    end
    return result
end

local function capture(phase)
    local version, build, _, interface = GetBuildInfo()
    local spellBank = Enum and Enum.SpellBookSpellBank and Enum.SpellBookSpellBank.Player or 0
    local mapId
    if C_Map and C_Map.GetBestMapForUnit then
        local ok, result = pcall(C_Map.GetBestMapForUnit, "player")
        if ok then mapId = result end
    end
    local mapPosition
    if mapId and C_Map and C_Map.GetPlayerMapPosition then
        local ok, result = pcall(C_Map.GetPlayerMapPosition, mapId, "player")
        if ok then mapPosition = result end
    end
    local aura
    if C_UnitAuras and C_UnitAuras.GetAuraDataByIndex then
        local ok, result = pcall(C_UnitAuras.GetAuraDataByIndex, "player", 1, "HELPFUL")
        if ok and type(result) == "table" then
            aura = result
        end
    end
    local spellBookItem
    if C_SpellBook and C_SpellBook.GetSpellBookItemInfo then
        local ok, result = pcall(C_SpellBook.GetSpellBookItemInfo, 1, spellBank)
        if ok and type(result) == "table" then
            spellBookItem = result
        end
    end
    local bagItem
    if C_Container and C_Container.GetContainerItemInfo then
        local ok, result = pcall(C_Container.GetContainerItemInfo, 0, 1)
        if ok and type(result) == "table" then
            bagItem = result
        end
    end
    local cooldown
    if C_Spell and C_Spell.GetSpellCooldown then
        local ok, result = pcall(C_Spell.GetSpellCooldown, 6603)
        if ok and type(result) == "table" then
            cooldown = result
        end
    end
    local result = {
        version = version,
        build = build,
        interface = interface,
        project = WOW_PROJECT_ID,
        mainlineProject = WOW_PROJECT_MAINLINE,
        inCombat = InCombatLockdown() and true or false,
        playerHealth = status(UnitHealth, "player"),
        playerHealthPercent = status(UnitHealthPercent, "player"),
        playerHealthMax = status(UnitHealthMax, "player"),
        playerPower = status(UnitPower, "player"),
        playerPowerPercent = status(UnitPowerPercent, "player"),
        playerHealthBar = statusBarStatus("player", "health"),
        playerPowerBar = statusBarStatus("player", "power"),
        targetHealth = status(UnitHealth, "target"),
        targetHealthPercent = status(UnitHealthPercent, "target"),
        targetPower = status(UnitPower, "target"),
        targetHealthBar = statusBarStatus("target", "health"),
        playerAura = status(UnitAura, "player", 1, "HELPFUL"),
        playerAuraModern = status(C_UnitAuras and C_UnitAuras.GetAuraDataByIndex,
            "player", 1, "HELPFUL"),
        playerAuraBySpell = status(C_UnitAuras and
            C_UnitAuras.GetPlayerAuraBySpellID, 19740),
        playerAuraList = status(C_UnitAuras and C_UnitAuras.GetUnitAuras,
            "player", "HELPFUL"),
        playerAuraListCount = status(function()
            return #C_UnitAuras.GetUnitAuras("player", "HELPFUL")
        end),
        playerAuraInstances = status(C_UnitAuras and
            C_UnitAuras.GetUnitAuraInstanceIDs,
            "player", "HELPFUL"),
        playerAuraInstanceCount = status(function()
            return #C_UnitAuras.GetUnitAuraInstanceIDs("player", "HELPFUL")
        end),
        playerAuraSpellId = valueStatus(aura and aura.spellId),
        playerAuraName = valueStatus(aura and aura.name),
        playerAuraExpiration = valueStatus(aura and aura.expirationTime),
        unitPosition = status(UnitPosition, "player"),
        mapId = status(C_Map and C_Map.GetBestMapForUnit, "player"),
        mapPosition = valueStatus(mapPosition),
        mapPositionX = status(function()
            local x = mapPosition:GetXY()
            return x
        end),
        mapPositionY = status(function()
            local _, y = mapPosition:GetXY()
            return y
        end),
        worldPositionY = status(function()
            local _, y = UnitPosition("player")
            return y
        end),
        worldPositionZ = status(function()
            local _, _, z = UnitPosition("player")
            return z
        end),
        worldPositionMap = status(function()
            local _, _, _, map = UnitPosition("player")
            return map
        end),
        spellInfo = status(GetSpellInfo, 6603),
        modernSpellInfo = status(C_Spell and C_Spell.GetSpellInfo, 6603),
        modernSpellCooldown = status(C_Spell and C_Spell.GetSpellCooldown, 6603),
        cooldownStart = valueStatus(cooldown and cooldown.startTime),
        cooldownDuration = valueStatus(cooldown and cooldown.duration),
        spellCooldownDurationObject = status(C_Spell and C_Spell.GetSpellCooldownDuration,
            6603, true),
        spellCooldownRemaining = status(function()
            local duration = C_Spell.GetSpellCooldownDuration(6603, true)
            return duration:GetRemainingDuration()
        end),
        cooldownTimerBar = cooldownDisplayStatus(cooldownTimer, "SetTimerDuration"),
        cooldownRadialFrame = cooldownDisplayStatus(cooldownRadial,
            "SetCooldownFromDurationObject"),
        spellBook = status(GetNumSpellTabs),
        spellBookBankEnum = valueStatus(Enum and Enum.SpellBookSpellBank
            and Enum.SpellBookSpellBank.Player),
        spellBookGlobal = valueStatus(_G.GetNumSpellTabs),
        spellBookGlobalItem = status(GetSpellBookItemInfo, 1, "spell"),
        dataToColorIsForever = status(DataToColor and DataToColor.IsForever),
        modernSpellBookLines = status(C_SpellBook and C_SpellBook.GetNumSpellBookSkillLines),
        modernSpellBookLine = status(C_SpellBook and C_SpellBook.GetSpellBookSkillLineInfo, 1),
        modernSpellBookItem = status(C_SpellBook and C_SpellBook.GetSpellBookItemInfo, 1, spellBank),
        spellBookItemId = valueStatus(spellBookItem and spellBookItem.spellID),
        spellBookItemType = valueStatus(spellBookItem and spellBookItem.itemType),
        modernSpellBookName = status(C_SpellBook and C_SpellBook.GetSpellBookItemName, 1, spellBank),
        modernSpellBookTexture = status(C_SpellBook and C_SpellBook.GetSpellBookItemTexture, 1, spellBank),
        actionInfo = status(GetActionInfo, 1),
        actionCooldown = status(GetActionCooldown, 1),
        usableAction = status(IsUsableAction, 1),
        powerType = status(UnitPowerType, "player"),
        bagSlots = status(C_Container and C_Container.GetContainerNumSlots, 0),
        bagItem = status(C_Container and C_Container.GetContainerItemInfo, 0, 1),
        bagItemId = valueStatus(bagItem and bagItem.itemID),
        bagItemCount = valueStatus(bagItem and bagItem.stackCount),
        playerFacing = status(GetPlayerFacing),
        unitGuid = status(UnitGUID, "target"),
        dataToColorLoaded = C_AddOns and C_AddOns.IsAddOnLoaded
            and C_AddOns.IsAddOnLoaded("DataToColor") or false,
        dataToColorClientVersion = DataToColor and DataToColor.ClientVersion or nil,
    }
    local platesOk, plates = pcall(nameplateProbe)
    result.nameplates = platesOk and plates or ("error: " .. tostring(plates))

    ForeverProbeResults = ForeverProbeResults or {}
    ForeverProbeResults[phase] = result
    print("ForeverProbe " .. phase .. ": interface " .. tostring(interface)
        .. ", project " .. tostring(WOW_PROJECT_ID)
        .. ", player health " .. result.playerHealth
        .. ", target health " .. result.targetHealth
        .. ", nameplates " .. tostring(platesOk and plates.count or "error"))
end

probe:RegisterEvent("PLAYER_ENTERING_WORLD")
probe:RegisterEvent("PLAYER_REGEN_DISABLED")
probe:RegisterEvent("PLAYER_REGEN_ENABLED")
probe:SetScript("OnEvent", function(_, event)
    if event == "PLAYER_ENTERING_WORLD" then
        capture("world")
    elseif event == "PLAYER_REGEN_DISABLED" then
        -- PLAYER_REGEN_DISABLED may fire before combat lockdown is effective.
        -- Delay the snapshot so it actually tests protected combat values.
        C_Timer.After(0.5, function()
            if InCombatLockdown() then
                capture("combatLocked")
            end
        end)
        -- A mob's own combat, threat and target can lag the player's.
        C_Timer.After(3, function()
            if InCombatLockdown() then
                capture("combatLater")
            end
        end)
    elseif event == "PLAYER_REGEN_ENABLED" then
        capture("afterCombat")
    end
end)

SLASH_FOREVERPROBE1 = "/foreverprobe"
SlashCmdList.FOREVERPROBE = function()
    capture("manual")
end

-- Counts, never units: whether nameplates come and go, whether the
-- behind-camera event carries a readable flag, whether hovering a plate makes
-- it the mouseover unit, and whether a click on it then selects that unit.
-- A person hovers and clicks; this frame only watches.
local plateEvents = {
    added = 0, removed = 0, behindChanged = 0, behindReadable = 0,
    mouseoverPlate = 0, mouseoverOther = 0, targetChanged = 0,
    targetFromHoveredPlate = 0, errors = 0,
}
local hoveredPlate, hoveredAt

local function hoveredPlateUnit()
    local plates = C_NamePlate.GetNamePlates()
    if isSecret(plates) or type(plates) ~= "table" then return end
    for _, plate in ipairs(plates) do
        local unit = plateUnit(plate)
        if unit then
            local same = UnitIsUnit(unit, "mouseover")
            if not isSecret(same) and same == true then return unit end
        end
    end
end

local plateWatcher = CreateFrame("Frame")
for _, event in ipairs({ "PLAYER_ENTERING_WORLD", "NAME_PLATE_UNIT_ADDED",
    "NAME_PLATE_UNIT_REMOVED", "NAME_PLATE_UNIT_BEHIND_CAMERA_CHANGED",
    "UPDATE_MOUSEOVER_UNIT", "PLAYER_TARGET_CHANGED" }) do
    pcall(plateWatcher.RegisterEvent, plateWatcher, event)
end
plateWatcher:SetScript("OnEvent", function(_, event, ...)
    local ok = pcall(function(...)
        if event == "PLAYER_ENTERING_WORLD" then
            ForeverProbeResults = ForeverProbeResults or {}
            ForeverProbeResults.nameplateEvents = plateEvents
        elseif event == "NAME_PLATE_UNIT_ADDED" then
            plateEvents.added = plateEvents.added + 1
        elseif event == "NAME_PLATE_UNIT_REMOVED" then
            plateEvents.removed = plateEvents.removed + 1
        elseif event == "NAME_PLATE_UNIT_BEHIND_CAMERA_CHANGED" then
            local _, isBehind = ...
            plateEvents.behindChanged = plateEvents.behindChanged + 1
            if not isSecret(isBehind) and type(isBehind) == "boolean" then
                plateEvents.behindReadable = plateEvents.behindReadable + 1
            end
        elseif event == "UPDATE_MOUSEOVER_UNIT" then
            local unit = hoveredPlateUnit()
            if unit then
                hoveredPlate, hoveredAt = unit, GetTime()
                plateEvents.mouseoverPlate = plateEvents.mouseoverPlate + 1
            else
                plateEvents.mouseoverOther = plateEvents.mouseoverOther + 1
            end
        elseif event == "PLAYER_TARGET_CHANGED" then
            plateEvents.targetChanged = plateEvents.targetChanged + 1
            if hoveredPlate and GetTime() - hoveredAt < 3 then
                local same = UnitIsUnit(hoveredPlate, "target")
                if not isSecret(same) and same == true then
                    plateEvents.targetFromHoveredPlate =
                        plateEvents.targetFromHoveredPlate + 1
                end
            end
        end
    end, ...)
    if not ok then
        plateEvents.errors = plateEvents.errors + 1
    end
end)

-- Static IDs only: no character name, macro body, item name, or game input.
-- The catalog is saved by WoW on /reload so the port can inspect the modern
-- spellbook/action-bar layouts without enabling the old pixel runtime.
local readableNumber = events.ReadableNumber

local function readableString(value)
    if isSecret(value) then
        return nil
    end
    return type(value) == "string" and value or nil
end

local function buildCatalog()
    if InCombatLockdown() then
        print("ForeverProbe catalog: leave combat and try again")
        return
    end

    local _, build, _, interface = GetBuildInfo()
    local catalog = {
        build = build,
        interface = interface,
        actionsScanned = 120,
        actions = {},
        spellBookLines = {},
        spells = {},
        skipped = 0,
    }

    for slot = 1, catalog.actionsScanned do
        local ok, actionType, id = pcall(GetActionInfo, slot)
        if ok then
            local kind = readableString(actionType)
            local actionId = readableNumber(id)
            if kind and actionId then
                catalog.actions[slot] = { kind = kind, id = actionId }
            elseif kind or actionId then
                catalog.skipped = catalog.skipped + 1
            end
        else
            catalog.skipped = catalog.skipped + 1
        end
    end

    local bank = Enum.SpellBookSpellBank.Player
    local lineCount = readableNumber(C_SpellBook.GetNumSpellBookSkillLines())
    if not lineCount or lineCount < 0 or lineCount > 40 then
        print("ForeverProbe catalog: spellbook line count unavailable")
        return
    end

    for lineIndex = 1, lineCount do
        local ok, line = pcall(C_SpellBook.GetSpellBookSkillLineInfo, lineIndex)
        if ok and valueStatus(line) == "table" then
            local offset = readableNumber(line.itemIndexOffset)
            local count = readableNumber(line.numSpellBookItems)
            if offset and count and offset >= 0 and count >= 0 and
                offset + count <= 2000 then
                catalog.spellBookLines[#catalog.spellBookLines + 1] = {
                    index = lineIndex, offset = offset, count = count,
                }
                for spellSlot = offset + 1, offset + count do
                    local itemOk, item = pcall(C_SpellBook.GetSpellBookItemInfo,
                        spellSlot, bank)
                    if itemOk and valueStatus(item) == "table" then
                        local itemType = readableNumber(item.itemType)
                        local actionId = readableNumber(item.actionID)
                        local spellId = readableNumber(item.spellID)
                        if itemType and actionId then
                            catalog.spells[#catalog.spells + 1] = {
                                slot = spellSlot,
                                itemType = itemType,
                                actionId = actionId,
                                spellId = spellId,
                            }
                        else
                            catalog.skipped = catalog.skipped + 1
                        end
                    else
                        catalog.skipped = catalog.skipped + 1
                    end
                end
            else
                catalog.skipped = catalog.skipped + 1
            end
        else
            catalog.skipped = catalog.skipped + 1
        end
    end

    ForeverProbeCatalog = catalog
    print("ForeverProbe catalog: " .. #catalog.spells .. " spellbook items, "
        .. #catalog.spellBookLines .. " lines; " .. catalog.skipped
        .. " unreadable entries")
end

SLASH_FOREVERCATALOG1 = "/forevercatalog"
SlashCmdList.FOREVERCATALOG = function()
    local ok, err = pcall(buildCatalog)
    if not ok then
        print("ForeverProbe catalog: an API check failed; no catalog was saved: "
            .. tostring(err))
    end
end
