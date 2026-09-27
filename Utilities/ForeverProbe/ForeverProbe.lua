local probe = CreateFrame("Frame")
local bars = {}
local visual
local cooldownSlot = 1
local cooldownTimer = CreateFrame("StatusBar")
local cooldownRadial = CreateFrame("Cooldown")
local actionKindCodes = { spell = 1, item = 2, macro = 3, flyout = 4 }

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
    if type(issecretvalue) == "function" and issecretvalue(value) then
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
    else
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
local function readableNumber(value)
    if type(issecretvalue) == "function" and issecretvalue(value) then
        return nil
    end
    return type(value) == "number" and value or nil
end

local function readableString(value)
    if type(issecretvalue) == "function" and issecretvalue(value) then
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

-- Optional fixed-position visual channel prototype. The colored fills are
-- rendered by native StatusBars; Lua never reads or transforms secret values.
-- The white and yellow rows are 100% and 50% calibration references.
local function createVisual()
    local frame = CreateFrame("Frame", nil, UIParent)
    frame:SetSize(320, 190)
    frame:SetPoint("TOPLEFT", UIParent, "TOPLEFT", 16, -70)

    local function addBar(row, red, green, blue)
        local bar = CreateFrame("StatusBar", nil, frame)
        bar:SetSize(320, 10)
        bar:SetPoint("TOPLEFT", frame, "TOPLEFT", 0, -row * 17)
        bar:SetStatusBarTexture("Interface\\Buttons\\WHITE8X8")
        bar:SetStatusBarColor(red, green, blue)
        local background = bar:CreateTexture(nil, "BACKGROUND")
        background:SetAllPoints(bar)
        background:SetColorTexture(0, 0, 0, 1)
        return bar
    end

    frame.health = addBar(0, 0, 1, 0)
    frame.power = addBar(1, 0, 0, 1)
    frame.target = addBar(2, 1, 0, 0)
    frame.half = addBar(3, 1, 1, 0)
    frame.full = addBar(4, 1, 1, 1)
    -- A separate cyan row shows the *remaining fraction* of the selected
    -- action's cooldown. The timer is rendered entirely by the native widget.
    frame.cooldown = addBar(7, 0, 1, 1)
    frame.half:SetMinMaxValues(0, 1)
    frame.half:SetValue(0.5)
    frame.full:SetMinMaxValues(0, 1)
    frame.full:SetValue(1)

    -- Five small RGB pixels beneath the bars carry only ordinary map values.
    -- The last pixel is a validity marker written only after every field is
    -- encoded. If any API returns a secret or errors, the marker stays black.
    local function addPixel(index)
        local pixel = frame:CreateTexture(nil, "ARTWORK")
        pixel:SetSize(10, 10)
        pixel:SetPoint("TOPLEFT", frame, "TOPLEFT", index * 20, -90)
        pixel:SetColorTexture(0, 0, 0, 1)
        return pixel
    end
    frame.mapIdPixel = addPixel(0)
    frame.mapXPixel = addPixel(1)
    frame.mapYPixel = addPixel(2)
    frame.facingPixel = addPixel(3)
    frame.positionValidPixel = addPixel(4)
    frame.cooldownValidPixel = addPixel(6)
    frame.targetPresentPixel = addPixel(7)
    frame.actionIdPixel = addPixel(8)
    frame.actionMetaPixel = addPixel(9)
    frame.actionGridValidPixel = addPixel(10)

    -- Forty tiny pixels per row (ID, marker per action), six rows total.
    -- The marker distinguishes an empty slot from an unreadable one.
    frame.actionGrid = {}
    for slot = 1, 120 do
        local column = (slot - 1) % 20
        local row = math.floor((slot - 1) / 20)
        local function gridPixel(x)
            local pixel = frame:CreateTexture(nil, "ARTWORK")
            pixel:SetSize(8, 8)
            pixel:SetPoint("TOPLEFT", frame, "TOPLEFT", x, -140 - row * 8)
            pixel:SetColorTexture(0, 0, 0, 1)
            return pixel
        end
        frame.actionGrid[slot] = {
            id = gridPixel(column * 16),
            meta = gridPixel(column * 16 + 8),
        }
    end

    local function setIntPixel(pixel, value)
        value = math.floor(value)
        pixel:SetColorTexture(
            math.floor(value / 65536) % 256 / 255,
            math.floor(value / 256) % 256 / 255,
            value % 256 / 255, 1)
    end

    local function renderAction(slot, idPixel, metaPixel, markerSlot)
        idPixel:SetColorTexture(0, 0, 0, 1)
        metaPixel:SetColorTexture(0, 0, 0, 1)
        pcall(function()
            local actionType, id = GetActionInfo(slot)
            if type(issecretvalue) == "function" and
                (issecretvalue(actionType) or issecretvalue(id)) then
                return
            end

            local kind = actionKindCodes[actionType]
            if actionType == nil and id == nil then
                kind, id = 0, 0
            end
            if not kind or type(id) ~= "number" or id < 0 or
                id > 16777215 or id ~= math.floor(id) then return end

            setIntPixel(idPixel, id)
            setIntPixel(metaPixel, 0xA50000 + markerSlot * 256 + kind)
        end)
    end

    local elapsedSinceUpdate = 0
    local elapsedSinceActionGrid = 0
    frame:SetScript("OnUpdate", function(_, elapsed)
        elapsedSinceUpdate = elapsedSinceUpdate + elapsed
        elapsedSinceActionGrid = elapsedSinceActionGrid + elapsed
        if elapsedSinceUpdate < 0.1 then return end
        elapsedSinceUpdate = 0
        local ok = pcall(function()
            frame.health:SetMinMaxValues(0, UnitHealthMax("player"))
            frame.health:SetValue(UnitHealth("player"))
            frame.power:SetMinMaxValues(0, UnitPowerMax("player"))
            frame.power:SetValue(UnitPower("player"))
            frame.target:SetMinMaxValues(0, UnitHealthMax("target"))
            frame.target:SetValue(UnitHealth("target"))
        end)
        if not ok then
            frame:Hide()
            print("ForeverProbe visual bars stopped: the client rejected an update")
        end

        frame.positionValidPixel:SetColorTexture(0, 0, 0, 1)
        pcall(function()
            local mapId = C_Map.GetBestMapForUnit("player")
            if not mapId or mapId <= 0 or mapId > 16777215 then return end
            local pos = C_Map.GetPlayerMapPosition(mapId, "player")
            if not pos then return end
            local x, y = pos:GetXY()
            local facing = GetPlayerFacing()
            if not x or not y or not facing or
                x < 0 or x > 1 or y < 0 or y > 1 or
                facing < 0 or facing > 6.284 then return end

            setIntPixel(frame.mapIdPixel, mapId)
            setIntPixel(frame.mapXPixel, x * 16777215)
            setIntPixel(frame.mapYPixel, y * 16777215)
            setIntPixel(frame.facingPixel, facing * 100000)
            frame.positionValidPixel:SetColorTexture(165 / 255, 90 / 255, 165 / 255, 1)
        end)

        frame.cooldownValidPixel:SetColorTexture(0, 0, 0, 1)
        local cooldownOk = pcall(function()
            local duration = C_ActionBar.GetActionCooldownDuration(cooldownSlot, true)
            frame.cooldown:SetTimerDuration(duration,
                Enum.StatusBarInterpolation.Immediate,
                Enum.StatusBarTimerDirection.RemainingTime)
        end)
        frame.cooldown:SetShown(cooldownOk)
        if cooldownOk then
            frame.cooldownValidPixel:SetColorTexture(0, 1, 1, 1)
        elseif not frame.cooldownErrorReported then
            frame.cooldownErrorReported = true
            print("ForeverProbe action cooldown unavailable for slot " .. cooldownSlot)
        end

        -- A separate pair identifies the watched action. Its marker encodes
        -- 0xA5, slot, kind; black means the API result was not readable. The
        -- ID pixel is never accepted by the reader without that marker.
        renderAction(cooldownSlot, frame.actionIdPixel,
            frame.actionMetaPixel, cooldownSlot)

        if elapsedSinceActionGrid >= 0.5 then
            elapsedSinceActionGrid = 0
            frame.actionGridValidPixel:SetColorTexture(0, 0, 0, 1)
            for slot = 1, 120 do
                local pair = frame.actionGrid[slot]
                renderAction(slot, pair.id, pair.meta, 0)
            end
            frame.actionGridValidPixel:SetColorTexture(
                90 / 255, 165 / 255, 90 / 255, 1)
        end

        -- Green means a target exists, red means no target, black means the
        -- presence check was not readable. Never branch on a secret GUID.
        frame.targetPresentPixel:SetColorTexture(0, 0, 0, 1)
        pcall(function()
            local guid = UnitGUID("target")
            if type(issecretvalue) == "function" and issecretvalue(guid) then
                return
            end
            if guid then
                frame.targetPresentPixel:SetColorTexture(0, 1, 0, 1)
            else
                frame.targetPresentPixel:SetColorTexture(1, 0, 0, 1)
            end
        end)
    end)
    frame:Hide()
    return frame
end

SLASH_FOREVERCOOLDOWN1 = "/forevercooldown"
SlashCmdList.FOREVERCOOLDOWN = function(message)
    local slot = tonumber(message)
    if not slot or slot ~= math.floor(slot) or slot < 1 or slot > 12 then
        print("ForeverProbe: use /forevercooldown 1 through 12")
        return
    end
    cooldownSlot = slot
    if visual then visual.cooldownErrorReported = false end
    print("ForeverProbe watching action cooldown in slot " .. slot)
end

SLASH_FOREVERVISUAL1 = "/forevervisual"
SlashCmdList.FOREVERVISUAL = function()
    visual = visual or createVisual()
    if visual:IsShown() then
        visual:Hide()
        print("ForeverProbe visual bars hidden")
    else
        visual.actionGridValidPixel:SetColorTexture(0, 0, 0, 1)
        visual:Show()
        print("ForeverProbe visual bars shown")
    end
end
