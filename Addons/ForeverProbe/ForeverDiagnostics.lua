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

    ForeverProbeResults = ForeverProbeResults or {}
    ForeverProbeResults[phase] = result
    print("ForeverProbe " .. phase .. ": interface " .. tostring(interface)
        .. ", project " .. tostring(WOW_PROJECT_ID)
        .. ", player health " .. result.playerHealth
        .. ", target health " .. result.targetHealth)
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
    elseif event == "PLAYER_REGEN_ENABLED" then
        capture("afterCombat")
    end
end)

SLASH_FOREVERPROBE1 = "/foreverprobe"
SlashCmdList.FOREVERPROBE = function()
    capture("manual")
end

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
