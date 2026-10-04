-- The fixed-position visual channel the bot reads. The colored fills are
-- rendered by native StatusBars; Lua never reads or transforms secret values.
-- The white and yellow rows are 100% and 50% calibration references. Every
-- encoded block ends in a checksum or marker pixel that stays black when any
-- value it covers was protected or unreadable.

-- ForeverEvents.lua and ForeverNameplates.lua load first (see the TOC).
local events = ForeverProbeEvents
local nameplates = ForeverProbeNameplates
local isSecret = events.IsSecret
local readableNumber = events.ReadableNumber
local floor = math.floor
local actionKindCodes = { spell = 1, item = 2, macro = 3, flyout = 4 }
local visual

-- Version of the nameplate roster rows; the reader refuses any other.
-- 2 added each plate's duel-distance flag, 3 its follow distance and the
-- unit's classification, 4 whether the unit targets the player's pet.
local ROSTER_VERSION = 4
-- Version of the pet block; the reader refuses any other.
local PET_VERSION = 1
-- Version of the minimap block; the reader refuses any other.
local MINIMAP_VERSION = 1
-- Version of the attack-state block; the reader refuses any other.
local ATTACK_VERSION = 1
-- Version of the soft-interact block; the reader refuses any other.
local INTERACT_VERSION = 1
-- The melee auto attack, as the client's spell.
local ATTACK_SPELL = 6603
-- CheckInteractDistance's duel distance. On this client it turns true as a
-- unit crosses a ranged weapon's 8-yard minimum range.
local DUEL_DISTANCE = 3
-- CheckInteractDistance's follow distance, 28 yards; it answers in combat.
local FOLLOW_DISTANCE = 4
-- The client's unit classifications as the roster codes them; 0 is unknown.
local CLASSIFICATION_CODES = { normal = 1, trivial = 2, minus = 3, rare = 4,
    elite = 5, rareelite = 6, worldboss = 7 }

-- A key that clears the target, for the bot. The client has no clear-target
-- binding of its own, and Escape opens the game menu whenever nothing is
-- selected. So a secure button runs /cleartarget and the first free key of a
-- short list is bound to it, out of combat, for this session only: an
-- override binding, so the player's saved key bindings never change. The
-- binding grid reports the key in DataToColor's clear-target row, which
-- otherwise needs the BindPad add-on.
local CLEAR_TARGET_BUTTON = "ForeverProbeClearTargetButton"
-- DataToColor's binding-grid row for its clear-target command (CUSTOM_CLEARTARGET).
local CLEAR_TARGET_INDEX = 58
-- Tried in order. Shift is the modifier the bot's key presses are known to
-- carry (Pet Attack and Start Attack use it).
local CLEAR_TARGET_KEYS = { "SHIFT-INSERT", "SHIFT-F12", "SHIFT-F11", "SHIFT-HOME", "SHIFT-END" }
-- DataToColor's codes for those keys (WoWKeyToId in SetupDefaultBindings.lua)
-- and for the Shift modifier.
local CLEAR_TARGET_KEY_IDS = { INSERT = 70, F12 = 63, F11 = 62, HOME = 71, END = 72 }
local SHIFT_CODE = 1
local clearTargetButton
local clearTargetKey

-- Creates the button and binds a free key, once per session; nothing is done
-- in combat, where both are protected.
local function bindClearTarget()
    if clearTargetKey or InCombatLockdown() then return end
    if not clearTargetButton then
        clearTargetButton = CreateFrame("Button", CLEAR_TARGET_BUTTON, UIParent,
            "SecureActionButtonTemplate")
        clearTargetButton:SetAttribute("type", "macro")
        clearTargetButton:SetAttribute("macrotext", "/cleartarget")
        -- A key binding clicks on key down or up, as ActionButtonUseKeyDown
        -- says; the template runs the macro once either way.
        clearTargetButton:RegisterForClicks("AnyUp", "AnyDown")
    end
    for _, key in ipairs(CLEAR_TARGET_KEYS) do
        local action = GetBindingAction(key, true)
        if action == nil or action == "" then
            SetOverrideBindingClick(clearTargetButton, true, key, CLEAR_TARGET_BUTTON, "LeftButton")
            clearTargetKey = key
            return
        end
    end
end

-- The clear-target row of the binding grid in DataToColor's encoding:
-- (key1 modifier << 22) | (index << 14) | (key1 id << 7); 0 while unbound.
local function clearTargetCode()
    if not clearTargetKey then return 0 end
    local id = CLEAR_TARGET_KEY_IDS[(clearTargetKey:gsub("^SHIFT%-", ""))]
    if not id then return 0 end
    return SHIFT_CODE * 4194304 + CLEAR_TARGET_INDEX * 16384 + id * 128
end

do
    local bindings = CreateFrame("Frame")
    bindings:RegisterEvent("PLAYER_ENTERING_WORLD")
    -- A login in combat binds once the fight is over.
    bindings:RegisterEvent("PLAYER_REGEN_ENABLED")
    bindings:SetScript("OnEvent", function()
        pcall(bindClearTarget)
    end)
end

local isCurrentAction = (C_ActionBar and C_ActionBar.IsCurrentAction) or IsCurrentAction
local isAutoRepeatAction = (C_ActionBar and C_ActionBar.IsAutoRepeatAction) or IsAutoRepeatAction
local isCurrentSpell = (C_Spell and C_Spell.IsCurrentSpell) or IsCurrentSpell

local function createVisual()
    local frame = CreateFrame("Frame", nil, UIParent)
    frame:SetSize(320, 190)
    frame:SetPoint("TOPLEFT", UIParent, "TOPLEFT", 16, -70)
    -- Above the nameplate markers, so a plate in this corner cannot cover a
    -- telemetry pixel. Children inherit the strata.
    frame:SetFrameStrata("TOOLTIP")
    frame:SetScript("OnHide", nameplates.HideMarkers)

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
    frame.half:SetMinMaxValues(0, 1)
    frame.half:SetValue(0.5)
    frame.full:SetMinMaxValues(0, 1)
    frame.full:SetValue(1)

    -- Small RGB pixels beneath the bars carry only ordinary values. The last
    -- pixel of each block is a validity marker written only after every field
    -- is encoded. If any API returns a secret or errors, the marker stays black.
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
    frame.targetPresentPixel = addPixel(7)
    frame.actionGridValidPixel = addPixel(10)
    frame.actionStateValidPixel = addPixel(15)
    frame.healthMaxPixel = addPixel(11)
    frame.healthMaxValidPixel = addPixel(12)
    frame.powerMaxPixel = addPixel(13)
    frame.powerMaxValidPixel = addPixel(14)
    -- Signed centiyards with a 2^23 bias fit each world axis in one RGB pixel.
    frame.worldXPixel = addPixel(0, 105)
    frame.worldYPixel = addPixel(1, 105)
    frame.worldZPixel = addPixel(2, 105)
    frame.worldMapPixel = addPixel(3, 105)
    frame.worldValidPixel = addPixel(4, 105)
    frame.spellCountPixel = addPixel(5, 105)
    frame.spellCountValidPixel = addPixel(6, 105)
    frame.bindingCountPixel = addPixel(7, 105)
    frame.bindingCountValidPixel = addPixel(8, 105)
    frame.powerTypePixel = addPixel(9, 105)
    frame.powerTypeValidPixel = addPixel(10, 105)
    frame.playerIdentityPixel = addPixel(11, 105)
    frame.playerIdentityValidPixel = addPixel(12, 105)
    frame.targetFlagsPixel = addPixel(13, 105)
    frame.targetFlagsValidPixel = addPixel(14, 105)
    frame.castSequencePixel = addPixel(0, 190)
    frame.castSpellPixel = addPixel(1, 190)
    frame.castValidPixel = addPixel(2, 190)
    frame.targetHash1Pixel = addPixel(3, 190)
    frame.targetHash2Pixel = addPixel(4, 190)
    frame.targetLevelPixel = addPixel(5, 190)
    frame.targetIdentityValidPixel = addPixel(6, 190)
    frame.targetWorldXPixel = addPixel(7, 190)
    frame.targetWorldYPixel = addPixel(8, 190)
    frame.targetWorldZPixel = addPixel(9, 190)
    frame.targetWorldMapPixel = addPixel(10, 190)
    frame.targetWorldValidPixel = addPixel(11, 190)
    frame.targetMapIdPixel = addPixel(12, 190)
    frame.targetMapXPixel = addPixel(13, 190)
    frame.targetMapYPixel = addPixel(14, 190)
    frame.targetMapValidPixel = addPixel(15, 190)
    frame.attackSlotsPixel = addPixel(17, 190)
    frame.attackStatePixel = addPixel(18, 190)
    frame.attackValidPixel = addPixel(19, 190)
    frame.auraStatePixel = addPixel(28, 190)
    frame.auraStateValidPixel = addPixel(29, 190)

    frame.deathPixels, frame.lootPixels, frame.errorPixels = {}, {}, {}
    for i = 1, 4 do frame.deathPixels[i] = addPixel(16 + i, 205) end
    for i = 1, 9 do frame.lootPixels[i] = addPixel(16 + i, 225) end
    for i = 1, 3 do frame.errorPixels[i] = addPixel(25 + i, 225) end
    frame.interactPixels = {}
    for i = 1, 5 do frame.interactPixels[i] = addPixel(21 + i, 205) end
    -- The player's pet, beside the roster header.
    frame.petPixels = {}
    for i = 1, 4 do frame.petPixels[i] = addPixel(21 + i, 245) end
    -- Where the minimap sits and how far it sees, after the world row's values.
    frame.minimapPixels = {}
    for i = 1, 6 do frame.minimapPixels[i] = addPixel(14 + i, 105) end

    -- The nameplate roster: a header with the plate counts and settings,
    -- then one checksummed row of seven values per slot.
    frame.rosterHeader = {}
    for i = 1, 4 do frame.rosterHeader[i] = addPixel(16 + i, 245) end
    frame.rosterRows = {}
    for slot = 1, nameplates.SlotCount do
        local row = {}
        for i = 1, 8 do row[i] = addPixel(16 + i, 260 + (slot - 1) * 15) end
        frame.rosterRows[slot] = row
    end

    -- Render permitted aura icons through native textures. In combat the
    -- spell-ID lookup may return nil for an active restricted aura, so nil
    -- cannot prove absence. The external reader compares these rendered
    -- icons with a reference icon per main-bar slot from the same capture, so
    -- "the buff of the spell on slot N is active" works for any class.
    local function addIcon(x, y)
        local icon = frame:CreateTexture(nil, "OVERLAY")
        icon:SetSize(16, 16)
        icon:SetPoint("TOPLEFT", frame, "TOPLEFT", x, y)
        icon:SetColorTexture(0, 0, 0, 1)
        return icon
    end
    frame.auraIcons = {}
    for index = 1, 40 do
        frame.auraIcons[index] = addIcon((index % 21) * 18,
            -700 - floor(index / 21) * 18)
    end
    -- Slot references continue the first aura row to its right.
    frame.auraReferences = {}
    for slot = 1, 12 do
        frame.auraReferences[slot] = addIcon((20 + slot) * 18, -700)
    end

    -- A grid of 8-unit pixel pairs, 20 pairs to a row: a value and its marker.
    -- The marker distinguishes an empty entry from an unreadable one.
    local function addGrid(count, top)
        local grid = {}
        for index = 1, count do
            local column = (index - 1) % 20
            local row = floor((index - 1) / 20)
            local pair = {}
            for part = 1, 2 do
                local pixel = frame:CreateTexture(nil, "ARTWORK")
                pixel:SetSize(8, 8)
                pixel:SetPoint("TOPLEFT", frame, "TOPLEFT",
                    column * 16 + (part - 1) * 8, -top - row * 8)
                pixel:SetColorTexture(0, 0, 0, 1)
                pair[part] = pixel
            end
            grid[index] = pair
        end
        return grid
    end
    -- Action type and ID for all 120 slots, the first 200 spellbook entries
    -- (a larger book is explicitly unreadable), 62 binding codes, and the
    -- usable/mana/range state of all 120 slots.
    frame.actionGrid = addGrid(120, 140)
    frame.spellGrid = addGrid(200, 205)
    frame.bindingGrid = addGrid(62, 300)
    frame.actionStateGrid = addGrid(120, 340)

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

    frame.bagHeaders = {}
    for bag = 0, 4 do
        frame.bagHeaders[bag] = {
            packed = addPixel(bag * 3, 500),
            family = addPixel(bag * 3 + 1, 500),
            marker = addPixel(bag * 3 + 2, 500),
        }
    end
    frame.bagGrid = {}
    for index = 0, 199 do
        local column = index % 10
        local row = floor(index / 10)
        local function pixel(offset)
            local p = frame:CreateTexture(nil, "ARTWORK")
            p:SetSize(8, 8)
            p:SetPoint("TOPLEFT", frame, "TOPLEFT",
                column * 30 + offset, -520 - row * 8)
            p:SetColorTexture(0, 0, 0, 1)
            return p
        end
        frame.bagGrid[index] = {
            id = pixel(0), count = pixel(10), marker = pixel(20),
        }
    end

    local function setIntPixel(pixel, value)
        value = floor(value)
        pixel:SetColorTexture(
            floor(value / 65536) % 256 / 255,
            floor(value / 256) % 256 / 255,
            value % 256 / 255, 1)
    end

    local function setBlack(pixel)
        pixel:SetColorTexture(0, 0, 0, 1)
    end

    -- Signed centiyards with a 2^23 bias, or nil for a protected or
    -- out-of-range axis. Shared by the player and target world positions.
    local function encodeAxis(value)
        if type(value) ~= "number" or isSecret(value) or
            value ~= value then return nil end
        local encoded = floor(value * 100 + 0.5) + 8388608
        if encoded < 0 or encoded > 16777215 then return nil end
        return encoded
    end

    -- Each step below runs under pcall: a protected value or a client error
    -- ends that step and leaves its marker black. The steps are defined once,
    -- so the update loop allocates no closures.

    local function updateBars()
        frame.health:SetMinMaxValues(0, UnitHealthMax("player"))
        frame.health:SetValue(UnitHealth("player"))
        frame.power:SetMinMaxValues(0, UnitPowerMax("player"))
        frame.power:SetValue(UnitPower("player"))
        frame.target:SetMinMaxValues(0, UnitHealthMax("target"))
        frame.target:SetValue(UnitHealth("target"))
    end

    local function renderAuras()
        local auras = C_UnitAuras.GetUnitAuras("player", "HELPFUL")
        for index = 1, 40 do
            local aura = auras[index]
            if aura then
                frame.auraIcons[index]:SetTexture(aura.icon)
            else
                frame.auraIcons[index]:SetColorTexture(0, 0, 0, 1)
            end
        end
        -- Refreshed with the auras so both come from the same capture.
        -- A protected texture still flows to the widget; an empty slot
        -- stays black, which the reader reports as unknown.
        for slot = 1, 12 do
            local texture = GetActionTexture(slot)
            if isSecret(texture) or texture then
                frame.auraReferences[slot]:SetTexture(texture)
            else
                frame.auraReferences[slot]:SetColorTexture(0, 0, 0, 1)
            end
        end
        local state = 3 -- slot reference grid; 2 was the Seal-only layout
        setIntPixel(frame.auraStatePixel, state)
        setIntPixel(frame.auraStateValidPixel, (0xA5B5A9 - state) % 16777216)
    end

    local function renderPosition()
        local mapId = C_Map.GetBestMapForUnit("player")
        if not mapId or mapId <= 0 or mapId > 16777215 then return end
        local pos = C_Map.GetPlayerMapPosition(mapId, "player")
        if not pos then return end
        local x, y = pos:GetXY()
        local facing = GetPlayerFacing()
        if not x or not y or not facing or
            x < 0 or x > 1 or y < 0 or y > 1 or
            facing < 0 or facing > 6.284 then return end

        local encodedX = floor(x * 16777215)
        local encodedY = floor(y * 16777215)
        local encodedFacing = floor(facing * 100000)
        setIntPixel(frame.mapIdPixel, mapId)
        setIntPixel(frame.mapXPixel, encodedX)
        setIntPixel(frame.mapYPixel, encodedY)
        setIntPixel(frame.facingPixel, encodedFacing)
        setIntPixel(frame.positionValidPixel,
            (0xA55AA5 - mapId - encodedX - encodedY - encodedFacing) % 16777216)
    end

    local function renderWorld()
        local x, y, z, instanceMap = UnitPosition("player")
        local encodedX, encodedY, encodedZ =
            encodeAxis(x), encodeAxis(y), encodeAxis(z)
        if not encodedX or not encodedY or not encodedZ or
            type(instanceMap) ~= "number" or isSecret(instanceMap) or
            instanceMap < 0 or instanceMap > 16777215 or
            instanceMap ~= floor(instanceMap) then return end
        setIntPixel(frame.worldXPixel, encodedX)
        setIntPixel(frame.worldYPixel, encodedY)
        setIntPixel(frame.worldZPixel, encodedZ)
        setIntPixel(frame.worldMapPixel, instanceMap)
        setIntPixel(frame.worldValidPixel,
            (0xA55A33 - encodedX - encodedY - encodedZ - instanceMap) % 16777216)
    end

    -- Maxima are ordinary numbers on some Forever builds. Encode each only
    -- when it is readable; current health/power remain secret and continue
    -- to flow exclusively through native StatusBars above.
    local function renderMaxValue(getter, valuePixel, markerPixel, marker)
        local maximum = readableNumber(getter("player"))
        if not maximum or maximum <= 0 or maximum > 16777215 or
            maximum ~= floor(maximum) then return end
        setIntPixel(valuePixel, maximum)
        setIntPixel(markerPixel, (marker - maximum) % 16777216)
    end

    local function renderMax(getter, valuePixel, markerPixel, marker)
        setBlack(valuePixel)
        setBlack(markerPixel)
        pcall(renderMaxValue, getter, valuePixel, markerPixel, marker)
    end

    local function renderPowerType()
        local powerType = readableNumber(UnitPowerType("player"))
        if powerType and powerType >= 0 and powerType <= 255 and
            powerType == floor(powerType) then
            setIntPixel(frame.powerTypePixel, powerType)
            setIntPixel(frame.powerTypeValidPixel,
                (0xA56644 - powerType) % 16777216)
        end
    end

    local function renderPlayerIdentity()
        local _, _, classId = UnitClass("player")
        local _, _, raceId = UnitRace("player")
        local level = UnitLevel("player")
        classId, raceId, level = readableNumber(classId),
            readableNumber(raceId), readableNumber(level)
        if not classId or not raceId or not level or
            classId < 1 or classId > 63 or
            raceId < 1 or raceId > 255 or
            level < 1 or level > 1023 or
            classId ~= floor(classId) or
            raceId ~= floor(raceId) or
            level ~= floor(level) then return end
        local packed = classId + raceId * 64 + level * 16384
        setIntPixel(frame.playerIdentityPixel, packed)
        setIntPixel(frame.playerIdentityValidPixel,
            (0xA57744 - packed) % 16777216)
    end

    -- The target identity this update checked. Every later target probe is
    -- checksummed with these hashes, so it can only describe the same unit.
    -- targetAbsent says this update read the flags and found no target.
    local checkedTargetGuid, checkedTargetHash1, checkedTargetHash2
    local targetAbsent = false

    local function renderTarget()
        local guid = UnitGUID("target")
        if isSecret(guid) then return end
        local present = guid ~= nil
        local attackable, dead = false, false
        if present then
            attackable = UnitCanAttack("player", "target")
            dead = UnitIsDead("target")
            if isSecret(attackable) or isSecret(dead) then return end
            if type(attackable) ~= "boolean" or
                type(dead) ~= "boolean" then return end
        end
        local playerCombat = UnitAffectingCombat("player") or false
        local playerDead = UnitIsDeadOrGhost("player") or false
        if isSecret(playerCombat) or isSecret(playerDead) then return end
        if type(playerCombat) ~= "boolean" or
            type(playerDead) ~= "boolean" then return end
        local flags = (present and 1 or 0) +
            (attackable and 2 or 0) + (dead and 4 or 0) +
            (playerCombat and 8 or 0) + (playerDead and 16 or 0) +
            32 -- protocol marker: older three-bit samples are rejected
        setIntPixel(frame.targetFlagsPixel, flags)
        setIntPixel(frame.targetFlagsValidPixel, (0xA57755 - flags) % 16777216)
        targetAbsent = not present

        local hash1, hash2, packedLevel = 0, 0, 0
        if present then
            local level = readableNumber(UnitLevel("target"))
            local isPlayer = UnitIsPlayer("target")
            if isSecret(isPlayer) then return end
            if type(guid) ~= "string" or #guid == 0 or
                not level or level < 1 or level > 255 or
                level ~= floor(level) or
                type(isPlayer) ~= "boolean" then return end
            hash1, hash2 = events.Hashes(guid)
            packedLevel = level * 2 + (isPlayer and 1 or 0)
        end
        setIntPixel(frame.targetHash1Pixel, hash1)
        setIntPixel(frame.targetHash2Pixel, hash2)
        setIntPixel(frame.targetLevelPixel, packedLevel)
        setIntPixel(frame.targetIdentityValidPixel,
            (0xA58866 - hash1 - hash2 - packedLevel) % 16777216)
        if present then
            checkedTargetGuid = guid
            checkedTargetHash1, checkedTargetHash2 = hash1, hash2
            events.ObserveTarget(guid)
        end
    end

    local function isCheckedTarget()
        local guid = UnitGUID("target")
        return not isSecret(guid) and guid == checkedTargetGuid
    end

    local function renderTargetWorld()
        if not isCheckedTarget() then return end
        local x, y, z, instanceMap = UnitPosition("target")
        local encodedX, encodedY, encodedZ =
            encodeAxis(x), encodeAxis(y), encodeAxis(z)
        if not encodedX or not encodedY or not encodedZ or
            type(instanceMap) ~= "number" or isSecret(instanceMap) or
            instanceMap < 0 or instanceMap > 16777215 or
            instanceMap ~= floor(instanceMap) then return end
        setIntPixel(frame.targetWorldXPixel, encodedX)
        setIntPixel(frame.targetWorldYPixel, encodedY)
        setIntPixel(frame.targetWorldZPixel, encodedZ)
        setIntPixel(frame.targetWorldMapPixel, instanceMap)
        setIntPixel(frame.targetWorldValidPixel,
            (0xA59977 - encodedX - encodedY - encodedZ - instanceMap -
                checkedTargetHash1 - checkedTargetHash2) % 16777216)
    end

    local function renderTargetMap()
        if not isCheckedTarget() then return end
        local mapId = readableNumber(C_Map.GetBestMapForUnit("target"))
        if not mapId or mapId <= 0 or mapId > 16777215 then return end
        local pos = C_Map.GetPlayerMapPosition(mapId, "target")
        if not pos or isSecret(pos) then return end
        local x, y = pos:GetXY()
        x, y = readableNumber(x), readableNumber(y)
        if not x or not y or x < 0 or x > 1 or
            y < 0 or y > 1 then return end
        local encodedX = floor(x * 16777215)
        local encodedY = floor(y * 16777215)
        setIntPixel(frame.targetMapIdPixel, mapId)
        setIntPixel(frame.targetMapXPixel, encodedX)
        setIntPixel(frame.targetMapYPixel, encodedY)
        setIntPixel(frame.targetMapValidPixel,
            (0xA5AA88 - mapId - encodedX - encodedY -
                checkedTargetHash1 - checkedTargetHash2) % 16777216)
    end

    -- A boolean API result as 0 unknown, 1 false, 2 true.
    local function tri(value)
        if isSecret(value) or type(value) ~= "boolean" then return 0 end
        return value and 2 or 1
    end

    -- Within one of CheckInteractDistance's distances as a tri-state. An
    -- error leaves only this flag unknown rather than the row or block it
    -- belongs to.
    local function distanceTri(unit, distance)
        local ok, value = pcall(CheckInteractDistance, unit, distance)
        return ok and tri(value) or 0
    end

    -- The unit's classification code, 0 when unknown, the same way.
    local function classificationCode(unit)
        local ok, value = pcall(UnitClassification, unit)
        if not ok or isSecret(value) then return 0 end
        return CLASSIFICATION_CODES[value] or 0
    end

    -- "nameplate3" -> "nameplate3target", built once per token.
    local targetOfToken = setmetatable({}, { __index = function(cache, token)
        local unit = token .. "target"
        cache[token] = unit
        return unit
    end })

    -- Action kinds of main-bar slots 1 to 12 from the last action grid; only
    -- a slot that holds an action is asked for its range to a plate.
    local mainBarKinds = {}
    for slot = 1, 12 do mainBarKinds[slot] = 0 end
    local rangeWeights = {}
    for slot = 1, 12 do rangeWeights[slot] = 4 ^ (slot - 1) end

    local plateValues = { 0, 0, 0, 0, 0, 0, 0 }

    -- One roster slot: incarnation, GUID hashes (0 while identity is
    -- protected), level and eight tri-state flags, then targets-player,
    -- reaction, threat, the plate's alpha, whether the unit is within duel
    -- and follow distance and its classification, then the main-bar ranges
    -- with the action-state codes (0 unknown, 1 in range, 2 out of range),
    -- then whether the unit targets the player's pet. The
    -- checksum names the slot and, for the target, the checked target hashes,
    -- so a row cannot describe another slot or another target.
    local function renderPlate(slot)
        local row = frame.rosterRows[slot]
        local v = plateValues
        v[1], v[2], v[3], v[4], v[5], v[6], v[7] = 0, 0, 0, 0, 0, 0, 0
        local token = nameplates.tokens[slot]
        local isTarget = 0
        if token then
            v[1] = nameplates.incarnations[slot]
            local guid = UnitGUID(token)
            if not isSecret(guid) and type(guid) == "string" and #guid > 0 then
                v[2], v[3] = events.Hashes(guid)
            end
            local level = readableNumber(UnitLevel(token))
            if not level or level < 1 or level > 255 or level ~= floor(level) then
                level = 0
            end
            if checkedTargetGuid then
                isTarget = tri(UnitIsUnit(token, "target"))
            elseif targetAbsent then
                isTarget = 1
            end
            local behind = tri(C_NamePlateManager.IsNamePlateUnitBehindCamera(token))
            v[4] = level +
                tri(UnitCanAttack("player", token)) * 256 +
                tri(UnitIsDead(token)) * 1024 +
                tri(UnitIsPlayer(token)) * 4096 +
                tri(UnitIsTapDenied(token)) * 16384 +
                tri(UnitAffectingCombat(token)) * 65536 +
                isTarget * 262144 +
                tri(UnitIsUnit(token, "mouseover")) * 1048576 +
                behind * 4194304

            local reaction = readableNumber(UnitReaction(token, "player"))
            if not reaction or reaction < 1 or reaction > 8 or
                reaction ~= floor(reaction) then
                reaction = 0
            end
            -- 0 unreadable, 1 not on the player's threat table, 2 to 5 status 0 to 3.
            local threat = 0
            local status = UnitThreatSituation("player", token)
            if not isSecret(status) then
                if status == nil then
                    threat = 1
                elseif type(status) == "number" and status >= 0 and status <= 3 then
                    threat = status + 2
                end
            end
            -- 0 unreadable, else 1 + 254 * alpha.
            local alpha = 0
            local plate = C_NamePlate.GetNamePlateForUnit(token)
            if not isSecret(plate) and plate then
                local value = readableNumber(plate:GetAlpha())
                if value and value >= 0 and value <= 1 then
                    alpha = floor(value * 254 + 0.5) + 1
                end
            end
            v[5] = tri(UnitIsUnit(targetOfToken[token], "player")) +
                reaction * 4 + threat * 64 + alpha * 512 +
                distanceTri(token, DUEL_DISTANCE) * 131072 +
                distanceTri(token, FOLLOW_DISTANCE) * 524288 +
                classificationCode(token) * 2097152

            local ranges = 0
            for action = 1, 12 do
                if mainBarKinds[action] ~= 0 then
                    local inRange = C_ActionBar.IsActionInRange(action, token)
                    if not isSecret(inRange) then
                        if inRange == true then
                            ranges = ranges + rangeWeights[action]
                        elseif inRange == false then
                            ranges = ranges + 2 * rangeWeights[action]
                        end
                    end
                end
            end
            v[6] = ranges
            -- A unit fighting the player's pet: a Hunter defends it.
            v[7] = tri(UnitIsUnit(targetOfToken[token], "pet"))
            -- A plate behind the camera is not drawn; its marker would be stale.
            nameplates.ShowMarker(slot, behind ~= 2)
        end

        local checksum = 0xA5C311 - slot
        for i = 1, 7 do
            setIntPixel(row[i], v[i])
            checksum = checksum - v[i]
        end
        if isTarget == 2 then
            checksum = checksum - checkedTargetHash1 - checkedTargetHash2
        end
        setIntPixel(row[8], checksum % 16777216)
    end

    -- The player's pet: whether it is there, dead and fighting, then the
    -- unit it attacks as identity hashes (0 with none), and the checksum.
    -- The minimap's centre and radius as shares of the screen (65535 is all
    -- of it; the centre measured from the top left), the yards its radius
    -- spans, and whether it turns with the player. The reader finds the dots
    -- of tracked units inside it, such as a Hunter's Track Beasts, and places
    -- them in the world.
    local function renderMinimap()
        local map = Minimap
        if not map or not C_Minimap or type(C_Minimap.GetViewRadius) ~= "function" then return end
        local uiScale = readableNumber(UIParent:GetEffectiveScale())
        local scale = readableNumber(map:GetEffectiveScale())
        local screenWidth, screenHeight = readableNumber(GetScreenWidth()), readableNumber(GetScreenHeight())
        local centerX, centerY = map:GetCenter()
        centerX, centerY = readableNumber(centerX), readableNumber(centerY)
        local width = readableNumber(map:GetWidth())
        local yards = readableNumber(C_Minimap.GetViewRadius())
        if not uiScale or not scale or not screenWidth or not screenHeight or not centerX or
            not centerY or not width or not yards or screenWidth <= 0 or screenHeight <= 0 or
            yards <= 0 then return end
        local screenX, screenY = screenWidth * uiScale, screenHeight * uiScale
        local x = floor(centerX * scale / screenX * 65535 + 0.5)
        local y = floor((1 - centerY * scale / screenY) * 65535 + 0.5)
        local radius = floor(width / 2 * scale / screenX * 65535 + 0.5)
        local view = floor(yards * 100 + 0.5)
        if x < 0 or x > 65535 or y < 0 or y > 65535 or radius <= 0 or radius > 65535 or
            view > 16777215 then return end
        local rotates = C_CVar.GetCVar("rotateMinimap") == "1" and 1 or 0
        local flags = rotates + MINIMAP_VERSION * 16
        local pixels = frame.minimapPixels
        setIntPixel(pixels[1], x)
        setIntPixel(pixels[2], y)
        setIntPixel(pixels[3], radius)
        setIntPixel(pixels[4], view)
        setIntPixel(pixels[5], flags)
        setIntPixel(pixels[6], (0xA5F122 - x - y - radius - view - flags) % 16777216)
    end

    local function renderPet()
        local exists = tri(UnitExists("pet"))
        local dead, combat, hash1, hash2 = 0, 0, 0, 0
        if exists == 2 then
            dead = tri(UnitIsDead("pet"))
            combat = tri(UnitAffectingCombat("pet"))
            local guid = UnitGUID("pettarget")
            if not isSecret(guid) and type(guid) == "string" and #guid > 0 then
                local first, second = events.Hashes(guid)
                if first then hash1, hash2 = first, second end
            end
        end
        local flags = exists + dead * 4 + combat * 16 + PET_VERSION * 64
        local pixels = frame.petPixels
        setIntPixel(pixels[1], flags)
        setIntPixel(pixels[2], hash1)
        setIntPixel(pixels[3], hash2)
        setIntPixel(pixels[4], (0xA5E411 - flags - hash1 - hash2) % 16777216)
    end

    -- How many plates the client shows (all, attackable, friendly players)
    -- and the settings that decide which plates exist.
    local function renderRosterHeader()
        local total, attackable, friendly = nameplates.Counts()
        local counts = math.min(total, 255) + math.min(attackable, 255) * 256 +
            math.min(friendly, 255) * 65536
        local distance = readableNumber(tonumber(C_CVar.GetCVar("nameplateMaxDistance")))
        distance = distance and math.max(0, math.min(floor(distance), 255)) or 0
        local settings = tri(C_CVar.GetCVarBool("nameplateShowEnemies")) +
            tri(C_CVar.GetCVarBool("nameplateShowAll")) * 4 + distance * 16
        local header = frame.rosterHeader
        setIntPixel(header[1], counts)
        setIntPixel(header[2], settings)
        setIntPixel(header[3], ROSTER_VERSION)
        setIntPixel(header[4],
            (0xA5C211 - counts - settings - ROSTER_VERSION) % 16777216)
    end

    local function renderRoster()
        local header = frame.rosterHeader
        for i = 1, 4 do setBlack(header[i]) end
        pcall(renderRosterHeader)
        for slot = 1, nameplates.SlotCount do
            local row = frame.rosterRows[slot]
            for i = 1, 8 do setBlack(row[i]) end
            -- Any failure leaves the row black and the marker hidden.
            nameplates.ShowMarker(slot, false)
            pcall(renderPlate, slot)
        end
    end

    local slotBits = {}
    for bit = 1, 24 do slotBits[bit] = 2 ^ (bit - 1) end

    -- A boolean the client may also answer as 1 or nil; nil when protected.
    local function plainFlag(value)
        if isSecret(value) then return nil end
        return value == true or value == 1
    end

    -- What the player's attacks are doing: each main-bar slot's current state
    -- (a cast, a toggle, or a next-swing ability pressed and waiting for the
    -- swing) and auto-repeat state (Auto Shot), and melee auto attack. Then
    -- whether the target is within duel distance. The checksum folds in the
    -- checked target hashes once that distance is known, so it can only
    -- describe this update's target.
    local function renderAttackState()
        local slots = 0
        for slot = 1, 12 do
            local current = plainFlag(isCurrentAction(slot))
            local repeating = plainFlag(isAutoRepeatAction(slot))
            if current == nil or repeating == nil then return end
            if current then slots = slots + slotBits[slot] end
            if repeating then slots = slots + slotBits[slot + 12] end
        end
        local close = 0
        if checkedTargetGuid and isCheckedTarget() then
            close = distanceTri("target", DUEL_DISTANCE)
        end
        local state = tri(isCurrentSpell(ATTACK_SPELL)) + close * 4 +
            ATTACK_VERSION * 16
        local checksum = 0xA5E111 - slots - state
        if close ~= 0 then
            checksum = checksum - checkedTargetHash1 - checkedTargetHash2
        end
        setIntPixel(frame.attackSlotsPixel, slots)
        setIntPixel(frame.attackStatePixel, state)
        setIntPixel(frame.attackValidPixel, checksum % 16777216)
    end

    -- The unit the client's Interact Key would act on (the soft-interact
    -- target), with whether it is dead and the player may loot it, then the
    -- two settings looting needs: the Interact Key for keyboard and mouse
    -- (SoftTargetInteract 3) and Auto Loot.
    local function renderSoftInteract()
        local guid = UnitGUID("softinteract")
        if isSecret(guid) then return end
        local hash1, hash2, unit = 0, 0, 0
        if guid then
            hash1, hash2 = events.Hashes(guid)
            if not hash1 then return end
            local lootable = 0
            local ok, hasLoot, canLoot = pcall(CanLootUnit, guid)
            if ok and not isSecret(hasLoot) and not isSecret(canLoot) and
                type(hasLoot) == "boolean" and type(canLoot) == "boolean" then
                lootable = (hasLoot and canLoot) and 2 or 1
            end
            unit = tri(UnitIsDead("softinteract")) + lootable * 4
        end
        local level = readableNumber(tonumber(C_CVar.GetCVar("SoftTargetInteract")))
        level = (level and level >= 0 and level <= 3 and level == floor(level)) and level + 1 or 0
        local settings = level + tri(C_CVar.GetCVarBool("autoLootDefault")) * 8 +
            INTERACT_VERSION * 64
        local pixels = frame.interactPixels
        setIntPixel(pixels[1], hash1)
        setIntPixel(pixels[2], hash2)
        setIntPixel(pixels[3], unit)
        setIntPixel(pixels[4], settings)
        setIntPixel(pixels[5], (0xA5E211 - hash1 - hash2 - unit - settings) % 16777216)
    end

    -- Type code and ID of an action slot, or nothing when unreadable.
    local function readAction(slot)
        local actionType, id = GetActionInfo(slot)
        if isSecret(actionType) or isSecret(id) then return end
        local kind = actionKindCodes[actionType]
        if actionType == nil and id == nil then
            kind, id = 0, 0
        end
        if not kind or type(id) ~= "number" or id < 0 or
            id > 16777215 or id ~= floor(id) then return end
        return kind, id
    end

    local function renderAction(slot, pair)
        setBlack(pair[1])
        setBlack(pair[2])
        local ok, kind, id = pcall(readAction, slot)
        if not ok or not kind then return end
        setIntPixel(pair[1], id)
        setIntPixel(pair[2], 0xA50000 + kind)
        return kind, id
    end

    -- Usable, no-mana and range of an action slot as one code, or nothing
    -- when unreadable. Range is 0 unknown, 1 in range, 2 out of range.
    local function readActionState(slot, kind)
        local usable, noMana, range = false, false, 0
        if kind ~= 0 then
            usable, noMana = IsUsableAction(slot)
            if isSecret(usable) or isSecret(noMana) then return end
            if usable == 1 then usable = true end
            if usable == 0 or usable == nil then usable = false end
            if noMana == 1 then noMana = true end
            if noMana == 0 or noMana == nil then noMana = false end
            if type(usable) ~= "boolean" or type(noMana) ~= "boolean" then
                return
            end
            local ok, inRange = pcall(C_ActionBar.IsActionInRange,
                slot, "target")
            if ok and not isSecret(inRange) then
                if inRange == 1 or inRange == true then range = 1
                elseif inRange == 0 or inRange == false then range = 2
                elseif inRange ~= nil then return end
            end
        end
        return 1 + (usable and 1 or 0) + (noMana and 2 or 0) + range * 4
    end

    local function renderActionState(slot, kind, id, pair)
        setBlack(pair[1])
        setBlack(pair[2])
        if not kind then return end
        local ok, code = pcall(readActionState, slot, kind)
        if not ok or not code then return end
        setIntPixel(pair[1], code)
        setIntPixel(pair[2], (0xA58811 - id - slot - kind - code) % 16777216)
    end

    -- The native timer bar shows the remaining cooldown fraction; Lua never
    -- sees the duration.
    local function setCooldownBar(bar, slot)
        local duration = C_ActionBar.GetActionCooldownDuration(slot, false)
        bar:SetTimerDuration(duration,
            Enum.StatusBarInterpolation.Immediate,
            Enum.StatusBarTimerDirection.RemainingTime)
    end

    local function renderActionGrid()
        setBlack(frame.actionGridValidPixel)
        setBlack(frame.actionStateValidPixel)
        for slot = 1, 120 do
            local kind, id = renderAction(slot, frame.actionGrid[slot])
            renderActionState(slot, kind, id, frame.actionStateGrid[slot])
            if slot <= 12 then
                mainBarKinds[slot] = kind or 0
                local cooldown = frame.cooldownGrid[slot]
                setBlack(cooldown.marker)
                cooldown.bar:Hide()
                if kind then
                    local cooldownReady = kind == 0 or
                        pcall(setCooldownBar, cooldown.bar, slot)
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

    -- Slot count, free slots and family of a bag, or nothing when unreadable.
    local function readBag(bag)
        local slots = readableNumber(C_Container.GetContainerNumSlots(bag))
        local free, family = C_Container.GetContainerNumFreeSlots(bag)
        free = readableNumber(free)
        family = family == nil and 0 or readableNumber(family)
        if not slots or not free or not family or slots < 0 or
            slots > 40 or free < 0 or free > slots or
            family < 0 or family > 16777215 then return end
        return slots, free, family
    end

    -- Item ID and count of a bag slot (0, 0 when empty), or nothing.
    local function readBagSlot(bag, slot, slots)
        local id, count = 0, 0
        if slot <= slots then
            local info = C_Container.GetContainerItemInfo(bag, slot)
            if info ~= nil then
                id = readableNumber(info.itemID)
                count = readableNumber(info.stackCount)
                if not id or not count or id <= 0 or
                    id > 16777215 or count <= 0 or
                    count > 65535 or id ~= floor(id) or
                    count ~= floor(count) then return end
            end
        end
        return id, count
    end

    local function renderBags()
        for bag = 0, 4 do
            local header = frame.bagHeaders[bag]
            setBlack(header.packed)
            setBlack(header.family)
            setBlack(header.marker)
            local ok, slots, free, family = pcall(readBag, bag)
            if ok and slots then
                local packed = slots + free * 64
                setIntPixel(header.packed, packed)
                setIntPixel(header.family, family)
                setIntPixel(header.marker,
                    (0xA5BA11 - bag - packed - family) % 16777216)
            else
                slots = nil
            end
            for slot = 1, 40 do
                local index = bag * 40 + slot - 1
                local cell = frame.bagGrid[index]
                setBlack(cell.id)
                setBlack(cell.count)
                setBlack(cell.marker)
                if slots then
                    local slotOk, id, count = pcall(readBagSlot, bag, slot, slots)
                    if slotOk and id then
                        setIntPixel(cell.id, id)
                        setIntPixel(cell.count, count)
                        setIntPixel(cell.marker,
                            (0xA5BB11 - index - id - count) % 16777216)
                    end
                end
            end
        end
    end

    local function renderSpellGrid()
        local lines = readableNumber(C_SpellBook.GetNumSpellBookSkillLines())
        if not lines or lines < 0 or lines > 40 or
            lines ~= floor(lines) then return end
        local count = 0
        for lineIndex = 1, lines do
            local info = C_SpellBook.GetSpellBookSkillLineInfo(lineIndex)
            if not info then return end
            local offset = readableNumber(info.itemIndexOffset)
            local length = readableNumber(info.numSpellBookItems)
            if not offset or not length or offset < 0 or length < 0 or
                offset ~= floor(offset) or
                length ~= floor(length) then return end
            count = math.max(count, offset + length)
        end
        if count > 200 then return end

        local bank = Enum.SpellBookSpellBank.Player
        for slot = 1, count do
            local pair = frame.spellGrid[slot]
            setBlack(pair[1])
            setBlack(pair[2])
            local item = C_SpellBook.GetSpellBookItemInfo(slot, bank)
            if not item or isSecret(item) then return end
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
            if id > 16777215 or id ~= floor(id) then return end
            local checksum = (0xA56611 - id - slot) % 16777216
            setIntPixel(pair[1], id)
            setIntPixel(pair[2], floor(checksum / 16) * 16 + source)
        end

        setIntPixel(frame.spellCountPixel, count)
        setIntPixel(frame.spellCountValidPixel, (0xA56622 - count) % 16777216)
    end

    -- DataToColor's compact binding codes; they need both add-ons installed.
    -- Its clear-target row needs BindPad; without it, the key bound to this
    -- add-on's own clear-target button fills the row.
    local function renderBindingGrid()
        if not DataToColor or
            type(DataToColor.GetBindingEncodedByIndex) ~= "function" then
            return
        end
        local boundCount = 0
        for index = 1, 62 do
            local code = readableNumber(
                DataToColor:GetBindingEncodedByIndex(index))
            if code == 0 and index == CLEAR_TARGET_INDEX then
                code = clearTargetCode()
            end
            if not code or code < 0 or code > 16777215 or
                code ~= floor(code) then return end
            if code > 0 then
                if floor(code / 16384) % 64 ~= index then return end
                boundCount = boundCount + 1
            end
            local pair = frame.bindingGrid[index]
            setIntPixel(pair[1], code)
            setIntPixel(pair[2], (0xA57711 - code - index) % 16777216)
        end
        if boundCount == 0 then return end
        setIntPixel(frame.bindingCountPixel, 62)
        setIntPixel(frame.bindingCountValidPixel, (0xA57722 - 62) % 16777216)
    end

    -- Green means a target exists, red means no target, black means the
    -- presence check was not readable. Never branch on a secret GUID.
    local function renderTargetPresence()
        local guid = UnitGUID("target")
        if isSecret(guid) then return end
        if guid then
            frame.targetPresentPixel:SetColorTexture(0, 1, 0, 1)
        else
            frame.targetPresentPixel:SetColorTexture(1, 0, 0, 1)
        end
    end

    local function renderEvents()
        local death = events.DeathPixels()
        for i = 1, #death do setIntPixel(frame.deathPixels[i], death[i]) end
        local loot = events.LootPixels()
        for i = 1, #loot do setIntPixel(frame.lootPixels[i], loot[i]) end
        local errors = events.ErrorPixels()
        for i = 1, #errors do setIntPixel(frame.errorPixels[i], errors[i]) end
        local cast = events.CastPixels()
        if cast then
            setIntPixel(frame.castSequencePixel, cast[1])
            setIntPixel(frame.castSpellPixel, cast[2])
            setIntPixel(frame.castValidPixel, cast[3])
        end
    end

    local elapsedSinceUpdate = 0
    local elapsedSinceActionGrid = 0
    local elapsedSinceSpellGrid = 0
    local elapsedSinceBindingGrid = 0
    local elapsedSinceBagGrid = 0

    local function onUpdate(_, elapsed)
        elapsedSinceUpdate = elapsedSinceUpdate + elapsed
        elapsedSinceActionGrid = elapsedSinceActionGrid + elapsed
        elapsedSinceSpellGrid = elapsedSinceSpellGrid + elapsed
        elapsedSinceBindingGrid = elapsedSinceBindingGrid + elapsed
        elapsedSinceBagGrid = elapsedSinceBagGrid + elapsed
        if elapsedSinceUpdate < 0.1 then return end
        elapsedSinceUpdate = 0

        if not pcall(updateBars) then
            frame:Hide()
            print("ForeverProbe visual bars stopped: the client rejected an update")
        end

        setBlack(frame.auraStateValidPixel)
        local auraOk, auraError = pcall(renderAuras)
        if not auraOk then
            ForeverProbeResults = ForeverProbeResults or {}
            ForeverProbeResults.auraGridError = tostring(auraError)
            if not frame.auraErrorReported then
                print("ForeverProbe aura grid: " .. tostring(auraError))
                frame.auraErrorReported = true
            end
        end

        setBlack(frame.positionValidPixel)
        pcall(renderPosition)
        setBlack(frame.worldValidPixel)
        pcall(renderWorld)
        renderMax(UnitHealthMax, frame.healthMaxPixel,
            frame.healthMaxValidPixel, 0xA55A11)
        renderMax(UnitPowerMax, frame.powerMaxPixel,
            frame.powerMaxValidPixel, 0xA55A22)
        setBlack(frame.powerTypePixel)
        setBlack(frame.powerTypeValidPixel)
        pcall(renderPowerType)
        setBlack(frame.playerIdentityPixel)
        setBlack(frame.playerIdentityValidPixel)
        pcall(renderPlayerIdentity)

        setBlack(frame.targetFlagsPixel)
        setBlack(frame.targetFlagsValidPixel)
        setBlack(frame.targetIdentityValidPixel)
        checkedTargetGuid, checkedTargetHash1, checkedTargetHash2 = nil, nil, nil
        targetAbsent = false
        pcall(renderTarget)
        setBlack(frame.targetWorldValidPixel)
        setBlack(frame.targetMapValidPixel)
        if checkedTargetGuid then
            pcall(renderTargetWorld)
            pcall(renderTargetMap)
        end
        -- After the target: the target's roster row and the attack state's
        -- distance are checked against it.
        renderRoster()
        setBlack(frame.attackSlotsPixel)
        setBlack(frame.attackStatePixel)
        setBlack(frame.attackValidPixel)
        pcall(renderAttackState)
        for i = 1, 5 do setBlack(frame.interactPixels[i]) end
        pcall(renderSoftInteract)
        for i = 1, 4 do setBlack(frame.petPixels[i]) end
        pcall(renderPet)
        setBlack(frame.minimapPixels[6])
        pcall(renderMinimap)

        setBlack(frame.castValidPixel)
        renderEvents()

        if elapsedSinceActionGrid >= 0.5 then
            elapsedSinceActionGrid = 0
            renderActionGrid()
        end
        if elapsedSinceBagGrid >= 1 then
            elapsedSinceBagGrid = 0
            renderBags()
        end
        if elapsedSinceSpellGrid >= 0.5 then
            elapsedSinceSpellGrid = 0
            setBlack(frame.spellCountValidPixel)
            pcall(renderSpellGrid)
        end
        if elapsedSinceBindingGrid >= 0.5 then
            elapsedSinceBindingGrid = 0
            setBlack(frame.bindingCountValidPixel)
            pcall(renderBindingGrid)
        end

        setBlack(frame.targetPresentPixel)
        pcall(renderTargetPresence)
    end

    frame:SetScript("OnUpdate", onUpdate)
    frame:Hide()
    return frame
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
