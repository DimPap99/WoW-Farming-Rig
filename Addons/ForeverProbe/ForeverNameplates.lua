-- The roster of attackable units the client draws a nameplate for, seen
-- without selecting them. Each tracked plate holds a slot, a fresh
-- incarnation number and a marker frame anchored at the plate's centre: Lua
-- cannot read a plate's screen position, so the bot finds the marker in the
-- captured frame. Slots follow the nameplate events; ForeverVisual.lua reads
-- them on every update and decides when a marker is shown. Nothing here
-- targets, moves or changes a setting.

-- ForeverEvents.lua loads first (see the TOC) and owns the shared helpers.
local events = ForeverProbeEvents
local isSecret = events.IsSecret
local plateUnit = events.PlateUnit
local GetTime = GetTime
local UnitCanAttack = UnitCanAttack
local UnitIsFriend = UnitIsFriend
local UnitIsPlayer = UnitIsPlayer
local UnitIsUnit = UnitIsUnit

local SLOT_COUNT = 8

-- Plate unit categories for the census counts.
local ATTACKABLE, FRIENDLY_PLAYER, OTHER = 1, 2, 3

local N = {
    SlotCount = SLOT_COUNT,
    tokens = {},        -- slot -> unit token, nil when free
    incarnations = {},  -- slot -> incarnation, 0 when free
    markers = {},
}
ForeverProbeNameplates = N

local anchored = {}     -- slot -> the marker follows its plate
local assignedAt = {}
local categories = {}   -- unit token -> category, for every plate shown
local counts = { 0, 0, 0 }
local sequence = 0

-- A 28-unit magenta square with a black ring and a 6-unit centre whose red
-- channel names the slot: 16 + 32 (slot - 1), green and blue full.
local function createMarker(slot)
    local marker = CreateFrame("Frame", nil, UIParent)
    marker:SetSize(28, 28)
    -- Below the telemetry panel, so a plate in the corner cannot cover it.
    marker:SetFrameStrata("FULLSCREEN_DIALOG")
    marker:EnableMouse(false)
    local border = marker:CreateTexture(nil, "OVERLAY")
    border:SetAllPoints(marker)
    border:SetColorTexture(1, 0, 1, 1)
    border:SetDrawLayer("OVERLAY", 0)
    local black = marker:CreateTexture(nil, "OVERLAY")
    black:SetSize(22, 22)
    black:SetPoint("CENTER", marker, "CENTER")
    black:SetColorTexture(0, 0, 0, 1)
    black:SetDrawLayer("OVERLAY", 1)
    local center = marker:CreateTexture(nil, "OVERLAY")
    center:SetSize(6, 6)
    center:SetPoint("CENTER", marker, "CENTER")
    center:SetColorTexture((16 + 32 * (slot - 1)) / 255, 1, 1, 1)
    center:SetDrawLayer("OVERLAY", 2)
    marker:Hide()
    return marker
end

for slot = 1, SLOT_COUNT do
    N.markers[slot] = createMarker(slot)
    N.incarnations[slot] = 0
    anchored[slot] = false
end

local function isTrue(value)
    return not isSecret(value) and value == true
end

local function category(token)
    if isTrue(UnitCanAttack("player", token)) then
        return ATTACKABLE
    end
    if isTrue(UnitIsPlayer(token)) and isTrue(UnitIsFriend("player", token)) then
        return FRIENDLY_PLAYER
    end
    return OTHER
end

local function slotOf(token)
    for slot = 1, SLOT_COUNT do
        if N.tokens[slot] == token then
            return slot
        end
    end
end

local function anchor(marker, token)
    local plate = C_NamePlate.GetNamePlateForUnit(token)
    if isSecret(plate) or not plate then
        error("no plate")
    end
    marker:SetPoint("CENTER", plate, "CENTER")
end

-- The marker stays hidden until the visual channel shows it.
local function assign(slot, token)
    sequence = sequence % 65535 + 1
    N.tokens[slot] = token
    N.incarnations[slot] = sequence
    assignedAt[slot] = GetTime()
    local marker = N.markers[slot]
    marker:Hide()
    marker:ClearAllPoints()
    anchored[slot] = pcall(anchor, marker, token)
end

local function release(slot)
    N.tokens[slot] = nil
    N.incarnations[slot] = 0
    anchored[slot] = false
    N.markers[slot]:Hide()
    N.markers[slot]:ClearAllPoints()
end

-- An attackable plate takes a free slot. The target always gets one, taking
-- the slot held longest by another unit when all are used.
local function track(token)
    if slotOf(token) then
        return
    end
    local target = isTrue(UnitIsUnit(token, "target"))
    if not target and categories[token] ~= ATTACKABLE then
        return
    end
    local oldest
    for slot = 1, SLOT_COUNT do
        local held = N.tokens[slot]
        if not held then
            assign(slot, token)
            return
        end
        if target and not isTrue(UnitIsUnit(held, "target")) and
            (not oldest or assignedAt[slot] < assignedAt[oldest]) then
            oldest = slot
        end
    end
    if oldest then
        release(oldest)
        assign(oldest, token)
    end
end

local function added(token)
    local kind = category(token)
    categories[token] = kind
    counts[kind] = counts[kind] + 1
    track(token)
end

local function removed(token)
    local kind = categories[token]
    if kind then
        categories[token] = nil
        counts[kind] = counts[kind] - 1
    end
    local slot = slotOf(token)
    if slot then
        release(slot)
    end
end

local function rebuild()
    for slot = 1, SLOT_COUNT do
        release(slot)
    end
    wipe(categories)
    counts[ATTACKABLE], counts[FRIENDLY_PLAYER], counts[OTHER] = 0, 0, 0
    local plates = C_NamePlate.GetNamePlates()
    if isSecret(plates) or type(plates) ~= "table" then
        return
    end
    for index = 1, #plates do
        local token = plateUnit(plates[index])
        if token then
            added(token)
        end
    end
end

-- The plates the client shows: all of them, the attackable ones, and
-- friendly players. A plate beyond the slots is counted but not tracked.
function N.Counts()
    return counts[ATTACKABLE] + counts[FRIENDLY_PLAYER] + counts[OTHER],
        counts[ATTACKABLE], counts[FRIENDLY_PLAYER]
end

-- Shows a slot's marker only while it is anchored to the slot's plate.
function N.ShowMarker(slot, visible)
    N.markers[slot]:SetShown(visible and anchored[slot])
end

function N.HideMarkers()
    for slot = 1, SLOT_COUNT do
        N.markers[slot]:Hide()
    end
end

local function onEvent(event, token)
    if event == "NAME_PLATE_UNIT_ADDED" or event == "NAME_PLATE_UNIT_REMOVED" then
        if isSecret(token) or type(token) ~= "string" then
            return
        end
        if event == "NAME_PLATE_UNIT_ADDED" then
            added(token)
        else
            removed(token)
        end
    elseif event == "PLAYER_TARGET_CHANGED" then
        -- A new target may have a plate without a slot yet.
        local plate = C_NamePlate.GetNamePlateForUnit("target")
        local target = not isSecret(plate) and plate and plateUnit(plate)
        if target then
            track(target)
        end
    elseif event == "PLAYER_ENTERING_WORLD" then
        rebuild()
    end
end

local watcher = CreateFrame("Frame")
watcher:RegisterEvent("PLAYER_ENTERING_WORLD")
watcher:RegisterEvent("PLAYER_TARGET_CHANGED")
watcher:RegisterEvent("NAME_PLATE_UNIT_ADDED")
watcher:RegisterEvent("NAME_PLATE_UNIT_REMOVED")
watcher:SetScript("OnEvent", function(_, event, token)
    local ok, err = pcall(onEvent, event, token)
    if not ok then
        N.lastError = tostring(err)
    end
end)
