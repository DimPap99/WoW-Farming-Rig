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
    local function addPixel(index, top)
        local pixel = frame:CreateTexture(nil, "ARTWORK")
        pixel:SetSize(10, 10)
        pixel:SetPoint("TOPLEFT", frame, "TOPLEFT", index * 20, -(top or 90))
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
    frame.actionStateValidPixel = addPixel(15)
    frame.healthMaxPixel = addPixel(11)
    frame.healthMaxValidPixel = addPixel(12)
    frame.powerMaxPixel = addPixel(13)
    frame.powerMaxValidPixel = addPixel(14)
    -- Signed centiyards with a 2^23 bias fit each world axis in one RGB pixel.
    -- This row sits between the map markers and the cooldown bar.
    frame.worldXPixel = addPixel(0, 105)
    frame.worldYPixel = addPixel(1, 105)
    frame.worldZPixel = addPixel(2, 105)
    frame.worldMapPixel = addPixel(3, 105)
    frame.worldValidPixel = addPixel(4, 105)
    frame.spellCountPixel = addPixel(5, 105)
    frame.spellCountValidPixel = addPixel(6, 105)
    frame.bindingCountPixel = addPixel(7, 105)
    frame.bindingCountValidPixel = addPixel(8, 105)

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

    -- Two pixels per spellbook slot: selected ID and a source/checksum marker.
    -- Only the first 200 slots fit; a larger book is explicitly unreadable.
    frame.spellGrid = {}
    for slot = 1, 200 do
        local column = (slot - 1) % 20
        local row = math.floor((slot - 1) / 20)
        local function gridPixel(x)
            local pixel = frame:CreateTexture(nil, "ARTWORK")
            pixel:SetSize(8, 8)
            pixel:SetPoint("TOPLEFT", frame, "TOPLEFT", x, -205 - row * 8)
            pixel:SetColorTexture(0, 0, 0, 1)
            return pixel
        end
        frame.spellGrid[slot] = {
            id = gridPixel(column * 16),
            meta = gridPixel(column * 16 + 8),
        }
    end

    frame.bindingGrid = {}
    for index = 1, 62 do
        local column = (index - 1) % 20
        local row = math.floor((index - 1) / 20)
        local function gridPixel(x)
            local pixel = frame:CreateTexture(nil, "ARTWORK")
            pixel:SetSize(8, 8)
            pixel:SetPoint("TOPLEFT", frame, "TOPLEFT", x, -300 - row * 8)
            pixel:SetColorTexture(0, 0, 0, 1)
            return pixel
        end
        frame.bindingGrid[index] = {
            code = gridPixel(column * 16),
            valid = gridPixel(column * 16 + 8),
        }
    end

    frame.actionStateGrid = {}
    for slot = 1, 120 do
        local column = (slot - 1) % 20
        local row = math.floor((slot - 1) / 20)
        local function statePixel(x)
            local pixel = frame:CreateTexture(nil, "ARTWORK")
            pixel:SetSize(8, 8)
            pixel:SetPoint("TOPLEFT", frame, "TOPLEFT", x, -340 - row * 8)
            pixel:SetColorTexture(0, 0, 0, 1)
            return pixel
        end
        frame.actionStateGrid[slot] = {
            code = statePixel(column * 16),
            valid = statePixel(column * 16 + 8),
        }
    end

    frame.cooldownGrid = {}
    for slot = 1, 12 do
        local bar = CreateFrame("StatusBar", nil, frame)
        bar:SetSize(320, 6)
        bar:SetPoint("TOPLEFT", frame, "TOPLEFT", 0,
            -400 - (slot - 1) * 8)
        bar:SetStatusBarTexture("Interface\\Buttons\\WHITE8X8")
        bar:SetStatusBarColor(0, 1, 1)
        local background = bar:CreateTexture(nil, "BACKGROUND")
        background:SetAllPoints(bar)
        background:SetColorTexture(0, 0, 0, 1)
        frame.cooldownGrid[slot] = {
            bar = bar,
            marker = addPixel(slot - 1, 130),
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
        local ok, kind, id = pcall(function()
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
            return kind, id
        end)
        if ok then return kind, id end
    end

    local function renderActionState(slot, kind, id, pair)
        pair.code:SetColorTexture(0, 0, 0, 1)
        pair.valid:SetColorTexture(0, 0, 0, 1)
        if not kind then return end
        pcall(function()
            local usable, noMana, range = false, false, 0
            if kind ~= 0 then
                usable, noMana = IsUsableAction(slot)
                if type(issecretvalue) == "function" and
                    (issecretvalue(usable) or issecretvalue(noMana)) then return end
                if usable == 1 then usable = true end
                if usable == 0 or usable == nil then usable = false end
                if noMana == 1 then noMana = true end
                if noMana == 0 or noMana == nil then noMana = false end
                if type(usable) ~= "boolean" or type(noMana) ~= "boolean" then
                    return
                end
                local ok, inRange = pcall(C_ActionBar.IsActionInRange,
                    slot, "target")
                if ok and (type(issecretvalue) ~= "function" or
                    not issecretvalue(inRange)) then
                    if inRange == 1 or inRange == true then range = 1
                    elseif inRange == 0 or inRange == false then range = 2
                    elseif inRange ~= nil then return end
                end
            end
            local code = 1 + (usable and 1 or 0) +
                (noMana and 2 or 0) + range * 4
            setIntPixel(pair.code, code)
            setIntPixel(pair.valid,
                (0xA58811 - id - slot - kind - code) % 16777216)
        end)
    end

    local elapsedSinceUpdate = 0
    local elapsedSinceActionGrid = 0
    local elapsedSinceSpellGrid = 0
    local elapsedSinceBindingGrid = 0
    frame:SetScript("OnUpdate", function(_, elapsed)
        elapsedSinceUpdate = elapsedSinceUpdate + elapsed
        elapsedSinceActionGrid = elapsedSinceActionGrid + elapsed
        elapsedSinceSpellGrid = elapsedSinceSpellGrid + elapsed
        elapsedSinceBindingGrid = elapsedSinceBindingGrid + elapsed
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

            local encodedX = math.floor(x * 16777215)
            local encodedY = math.floor(y * 16777215)
            local encodedFacing = math.floor(facing * 100000)
            setIntPixel(frame.mapIdPixel, mapId)
            setIntPixel(frame.mapXPixel, encodedX)
            setIntPixel(frame.mapYPixel, encodedY)
            setIntPixel(frame.facingPixel, encodedFacing)
            setIntPixel(frame.positionValidPixel,
                (0xA55AA5 - mapId - encodedX - encodedY - encodedFacing) % 16777216)
        end)

        frame.worldValidPixel:SetColorTexture(0, 0, 0, 1)
        pcall(function()
            local x, y, z, instanceMap = UnitPosition("player")
            local function encodeAxis(value)
                if type(value) ~= "number" or
                    (type(issecretvalue) == "function" and issecretvalue(value)) or
                    value ~= value then return nil end
                local encoded = math.floor(value * 100 + 0.5) + 8388608
                if encoded < 0 or encoded > 16777215 then return nil end
                return encoded
            end
            local encodedX, encodedY, encodedZ =
                encodeAxis(x), encodeAxis(y), encodeAxis(z)
            if not encodedX or not encodedY or not encodedZ or
                type(instanceMap) ~= "number" or
                (type(issecretvalue) == "function" and issecretvalue(instanceMap)) or
                instanceMap < 0 or instanceMap > 16777215 or
                instanceMap ~= math.floor(instanceMap) then return end
            setIntPixel(frame.worldXPixel, encodedX)
            setIntPixel(frame.worldYPixel, encodedY)
            setIntPixel(frame.worldZPixel, encodedZ)
            setIntPixel(frame.worldMapPixel, instanceMap)
            setIntPixel(frame.worldValidPixel,
                (0xA55A33 - encodedX - encodedY - encodedZ - instanceMap) % 16777216)
        end)

        -- Maxima are ordinary numbers on some Forever builds. Encode each
        -- only when it is readable; current health/power remain secret and
        -- continue to flow exclusively through native StatusBars above.
        local function renderMax(getter, valuePixel, markerPixel, marker)
            valuePixel:SetColorTexture(0, 0, 0, 1)
            markerPixel:SetColorTexture(0, 0, 0, 1)
            pcall(function()
                local maximum = readableNumber(getter("player"))
                if not maximum or maximum <= 0 or maximum > 16777215 or
                    maximum ~= math.floor(maximum) then return end
                setIntPixel(valuePixel, maximum)
                setIntPixel(markerPixel, (marker - maximum) % 16777216)
            end)
        end
        renderMax(UnitHealthMax, frame.healthMaxPixel,
            frame.healthMaxValidPixel, 0xA55A11)
        renderMax(UnitPowerMax, frame.powerMaxPixel,
            frame.powerMaxValidPixel, 0xA55A22)

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
            frame.actionStateValidPixel:SetColorTexture(0, 0, 0, 1)
            for slot = 1, 120 do
                local pair = frame.actionGrid[slot]
                local kind, id = renderAction(slot, pair.id, pair.meta, 0)
                renderActionState(slot, kind, id, frame.actionStateGrid[slot])
                if slot <= 12 then
                    local cooldown = frame.cooldownGrid[slot]
                    cooldown.marker:SetColorTexture(0, 0, 0, 1)
                    cooldown.bar:Hide()
                    if kind then
                        local cooldownReady = kind == 0 or pcall(function()
                            local duration =
                                C_ActionBar.GetActionCooldownDuration(slot, true)
                            cooldown.bar:SetTimerDuration(duration,
                                Enum.StatusBarInterpolation.Immediate,
                                Enum.StatusBarTimerDirection.RemainingTime)
                        end)
                        if cooldownReady then
                            cooldown.bar:SetShown(kind ~= 0)
                            setIntPixel(cooldown.marker,
                                (0xA59911 - slot - kind - id) % 16777216)
                        end
                    end
                end
            end
            frame.actionGridValidPixel:SetColorTexture(
                90 / 255, 165 / 255, 90 / 255, 1)
            frame.actionStateValidPixel:SetColorTexture(
                90 / 255, 136 / 255, 165 / 255, 1)
        end

        if elapsedSinceSpellGrid >= 0.5 then
            elapsedSinceSpellGrid = 0
            frame.spellCountValidPixel:SetColorTexture(0, 0, 0, 1)
            pcall(function()
                local lines = readableNumber(C_SpellBook.GetNumSpellBookSkillLines())
                if not lines or lines < 0 or lines > 40 or
                    lines ~= math.floor(lines) then return end
                local count = 0
                for lineIndex = 1, lines do
                    local info = C_SpellBook.GetSpellBookSkillLineInfo(lineIndex)
                    if not info then return end
                    local offset = readableNumber(info.itemIndexOffset)
                    local length = readableNumber(info.numSpellBookItems)
                    if not offset or not length or offset < 0 or length < 0 or
                        offset ~= math.floor(offset) or
                        length ~= math.floor(length) then return end
                    count = math.max(count, offset + length)
                end
                if count > 200 then return end

                local bank = Enum.SpellBookSpellBank.Player
                for slot = 1, count do
                    local pair = frame.spellGrid[slot]
                    pair.id:SetColorTexture(0, 0, 0, 1)
                    pair.meta:SetColorTexture(0, 0, 0, 1)
                    local item = C_SpellBook.GetSpellBookItemInfo(slot, bank)
                    if not item or
                        (type(issecretvalue) == "function" and issecretvalue(item)) then
                        return
                    end
                    local spellId = readableNumber(item.spellID)
                    local actionId = readableNumber(item.actionID)
                    local id, source
                    if spellId and spellId > 0 then
                        id, source = spellId, 1
                    elseif actionId and actionId > 0 then
                        id, source = actionId, 2
                    else
                        return
                    end
                    if id > 16777215 or id ~= math.floor(id) then return end
                    local checksum = (0xA56611 - id - slot) % 16777216
                    setIntPixel(pair.id, id)
                    setIntPixel(pair.meta,
                        math.floor(checksum / 16) * 16 + source)
                end

                setIntPixel(frame.spellCountPixel, count)
                setIntPixel(frame.spellCountValidPixel,
                    (0xA56622 - count) % 16777216)
            end)
        end

        if elapsedSinceBindingGrid >= 0.5 then
            elapsedSinceBindingGrid = 0
            frame.bindingCountValidPixel:SetColorTexture(0, 0, 0, 1)
            pcall(function()
                if not DataToColor or
                    type(DataToColor.GetBindingEncodedByIndex) ~= "function" then
                    return
                end
                local boundCount = 0
                for index = 1, 62 do
                    local code = readableNumber(
                        DataToColor:GetBindingEncodedByIndex(index))
                    if not code or code < 0 or code > 16777215 or
                        code ~= math.floor(code) then return end
                    if code > 0 then
                        if math.floor(code / 16384) % 64 ~= index then return end
                        boundCount = boundCount + 1
                    end
                    local pair = frame.bindingGrid[index]
                    setIntPixel(pair.code, code)
                    setIntPixel(pair.valid,
                        (0xA57711 - code - index) % 16777216)
                end
                if boundCount == 0 then return end
                setIntPixel(frame.bindingCountPixel, 62)
                setIntPixel(frame.bindingCountValidPixel,
                    (0xA57722 - 62) % 16777216)
            end)
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
