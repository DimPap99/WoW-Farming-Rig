-- Event latches survive target clearing and loot windows shorter than a capture.
-- This module observes the client; it never targets, moves, casts, or loots.
local E = {
    deathSequence = 0, deathHash1 = 0, deathHash2 = 0,
    opened = 0, closed = 0, cleared = 0, initialSlots = 0,
    remaining = 0, isOpen = false, pending = {}, supported = {},
    castSequence = 0, castSpellId = 0, castReadable = true,
}
ForeverProbeEvents = E

-- Resolved once: every protected-value check in the add-on goes through this.
local isSecret = type(issecretvalue) == "function" and issecretvalue or
    function() return false end
E.IsSecret = isSecret

local function readable(value)
    return not isSecret(value)
end

-- A plain number, or nil for a protected or non-numeric value.
function E.ReadableNumber(value)
    if isSecret(value) then return nil end
    return type(value) == "number" and value or nil
end

local function plateToken(plate)
    if type(plate.GetUnit) == "function" then
        return plate:GetUnit()
    end
    return plate.namePlateUnitToken
end

-- The unit token a nameplate frame shows, such as "nameplate3", or nil when
-- it is unreadable.
function E.PlateUnit(plate)
    local ok, unit = pcall(plateToken, plate)
    if ok and not isSecret(unit) and type(unit) == "string" then
        return unit
    end
end

-- The two target identity hashes. The C# reader recomputes nothing; it only
-- compares these, so every pixel that carries a GUID must use this function.
local function hashes(guid)
    if not readable(guid) or type(guid) ~= "string" or #guid == 0 then return end
    local h1, h2 = 5381, 7193
    for i = 1, #guid do
        local byte = string.byte(guid, i)
        h1 = (h1 * 33 + byte) % 16777216
        h2 = (h2 * 131 + byte) % 16777216
    end
    return h1, h2
end
E.Hashes = hashes

function E.ObserveTarget(guid)
    if hashes(guid) then E.lastTarget, E.lastTargetAt = guid, GetTime() end
end

local function died(guid, source)
    local h1, h2 = hashes(guid)
    if not h1 then return end
    -- The target-death and party-kill events can describe the same death.
    if E.deadGuid == guid then return end
    E.deadGuid = guid
    E.deathSequence = E.deathSequence % 65535 + 1
    E.deathHash1, E.deathHash2, E.deathSource = h1, h2, source
end

-- The player's own successful casts. A unit-filtered registration means the
-- client delivers only player events, so another unit's protected token can
-- never blank the latch. The unfiltered fallback keeps the old checks.
local function castSucceeded(unitFiltered, unit, ...)
    if not unitFiltered then
        if isSecret(unit) then
            E.castReadable = false
            return
        end
        if unit ~= "player" then return end
    end
    -- Payload shape varies by client. The last numeric argument is the spell
    -- ID on the supported event layouts.
    local spellId
    for i = select("#", ...), 1, -1 do
        local value = select(i, ...)
        if isSecret(value) then break end
        if type(value) == "number" then
            spellId = value
            break
        end
    end
    if type(spellId) ~= "number" or spellId <= 0 or spellId > 16777215 or
        spellId ~= math.floor(spellId) then
        E.castReadable = false
        return
    end
    E.castSequence = (E.castSequence % 65535) + 1
    E.castSpellId = spellId
    E.castReadable = true
end

local frame = CreateFrame("Frame")
for _, event in ipairs({ "PLAYER_ENTERING_WORLD", "PLAYER_TARGET_CHANGED",
    "PLAYER_TARGET_DIED", "PARTY_KILL", "LOOT_OPENED", "LOOT_CLOSED",
    "LOOT_SLOT_CLEARED" }) do
    E.supported[event] = pcall(frame.RegisterEvent, frame, event)
end
local castUnitFiltered = pcall(frame.RegisterUnitEvent, frame,
    "UNIT_SPELLCAST_SUCCEEDED", "player")
E.supported.UNIT_SPELLCAST_SUCCEEDED = castUnitFiltered or
    pcall(frame.RegisterEvent, frame, "UNIT_SPELLCAST_SUCCEEDED")

frame:SetScript("OnEvent", function(_, event, ...)
    local ok, err = pcall(function(...)
        if event == "UNIT_SPELLCAST_SUCCEEDED" then
            castSucceeded(castUnitFiltered, ...)
        elseif event == "PLAYER_ENTERING_WORLD" then
            ForeverProbeResults = ForeverProbeResults or {}
            ForeverProbeResults.lifecycle = E
            E.ObserveTarget(UnitGUID("target"))
        elseif event == "PLAYER_TARGET_CHANGED" then
            E.ObserveTarget(UnitGUID("target"))
        elseif event == "PLAYER_TARGET_DIED" then
            local guid = UnitGUID("target")
            if not hashes(guid) and E.lastTargetAt and GetTime() - E.lastTargetAt < 2 then
                guid = E.lastTarget
            end
            died(guid, event)
        elseif event == "PARTY_KILL" then
            died(select(2, ...), event)
        elseif event == "LOOT_OPENED" then
            E.opened = E.opened % 65535 + 1
            E.isOpen, E.remaining = true, 0
            wipe(E.pending)
            for slot = 1, GetNumLootItems() do
                if GetLootSlotType(slot) ~= 0 then
                    E.pending[slot] = true
                    E.remaining = E.remaining + 1
                end
            end
            E.initialSlots = E.remaining
        elseif event == "LOOT_SLOT_CLEARED" then
            local slot = ...
            if readable(slot) and E.pending[slot] then
                E.pending[slot] = nil
                E.remaining = math.max(0, E.remaining - 1)
                E.cleared = E.cleared % 65535 + 1
            end
        elseif event == "LOOT_CLOSED" then
            E.closed = E.closed % 65535 + 1
            E.isOpen = false
        end
    end, ...)
    if not ok then
        -- A cast event that failed to parse must not leave an old success
        -- looking current.
        if event == "UNIT_SPELLCAST_SUCCEEDED" then E.castReadable = false end
        E.lastError = tostring(err)
    end
end)

-- The pixel tables below are reused every capture; nothing here allocates
-- in the visual frame's OnUpdate.
local castPixels = { 0, 0, 0 }
local deathPixels = { 0, 0, 0, 0 }
local lootPixels = { 0, 0, 0, 0, 0, 0, 0, 0, 0 }

-- Sequence, spell ID and checksum, or nil while the latch is unreadable.
function E.CastPixels()
    if not E.castReadable then return nil end
    local a, b = E.castSequence, E.castSpellId
    castPixels[1], castPixels[2] = a, b
    castPixels[3] = (0xA5CC11 - a - b) % 16777216
    return castPixels
end

function E.DeathPixels()
    local a, b, c = E.deathSequence, E.deathHash1, E.deathHash2
    deathPixels[1], deathPixels[2], deathPixels[3] = a, b, c
    deathPixels[4] = (0xA5D611 - a - b - c) % 16777216
    return deathPixels
end

local function lootTarget()
    local guid = UnitGUID("target")
    local first, second = hashes(guid)
    if not first then return 0, 0, 0 end
    local lootable = 0
    local ok, hasLoot, canLoot = pcall(CanLootUnit, guid)
    if ok and readable(hasLoot) and readable(canLoot) and
        type(hasLoot) == "boolean" and type(canLoot) == "boolean" then
        lootable = hasLoot and canLoot and 2 or 1
    end
    return lootable, first, second
end

function E.LootPixels()
    local ok, lootable, h1, h2 = pcall(lootTarget)
    if not ok then lootable, h1, h2 = 0, 0, 0 end
    lootPixels[1], lootPixels[2], lootPixels[3] = E.opened, E.closed, E.cleared
    lootPixels[4] = E.initialSlots
    lootPixels[5] = E.remaining * 2 + (E.isOpen and 1 or 0)
    lootPixels[6], lootPixels[7], lootPixels[8] = lootable, h1, h2
    local checksum = 0xA5D722
    for i = 1, 8 do checksum = checksum - lootPixels[i] end
    lootPixels[9] = checksum % 16777216
    return lootPixels
end
