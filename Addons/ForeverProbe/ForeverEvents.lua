-- Event latches survive target clearing and loot windows shorter than a capture.
-- This module observes the client; it never targets, moves, casts, or loots.
local E = {
    deathSequence = 0, deathHash1 = 0, deathHash2 = 0,
    opened = 0, closed = 0, cleared = 0, initialSlots = 0,
    remaining = 0, isOpen = false, pending = {}, supported = {},
}
ForeverProbeEvents = E

local function readable(value)
    return not (type(issecretvalue) == "function" and issecretvalue(value))
end

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

local frame = CreateFrame("Frame")
for _, event in ipairs({ "PLAYER_ENTERING_WORLD", "PLAYER_TARGET_CHANGED",
    "PLAYER_TARGET_DIED", "PARTY_KILL", "LOOT_OPENED", "LOOT_CLOSED",
    "LOOT_SLOT_CLEARED" }) do
    E.supported[event] = pcall(frame.RegisterEvent, frame, event)
end
frame:SetScript("OnEvent", function(_, event, ...)
    local ok, err = pcall(function(...)
        if event == "PLAYER_ENTERING_WORLD" then
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
            E.isOpen, E.pending, E.remaining = true, {}, 0
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
    if not ok then E.lastError = tostring(err) end
end)

function E.DeathPixels()
    local a, b, c = E.deathSequence, E.deathHash1, E.deathHash2
    return { a, b, c, (0xA5D611 - a - b - c) % 16777216 }
end

function E.LootPixels()
    local lootable, h1, h2 = 0, 0, 0
    pcall(function()
        local guid = UnitGUID("target")
        local first, second = hashes(guid)
        if not first then return end
        h1, h2 = first, second
        local hasLoot, canLoot = CanLootUnit(guid)
        if readable(hasLoot) and readable(canLoot) and
            type(hasLoot) == "boolean" and type(canLoot) == "boolean" then
            lootable = hasLoot and canLoot and 2 or 1
        end
    end)
    local values = { E.opened, E.closed, E.cleared, E.initialSlots,
        E.remaining * 2 + (E.isOpen and 1 or 0), lootable, h1, h2 }
    local checksum = 0xA5D722
    for _, value in ipairs(values) do checksum = checksum - value end
    values[9] = checksum % 16777216
    return values
end
