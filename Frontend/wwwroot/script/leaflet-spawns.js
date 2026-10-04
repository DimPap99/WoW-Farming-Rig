// Points of interest: custom markers, teleports, NPC and node spawns, skinnables, mailboxes, NPCs by type and elites.
// Part of the Leaflet map, loaded after the pieces before it; see scripts/leaflet-pieces.mjs.

// Coordinate Marker Control moved to sidebar

// Storage for custom coordinate markers
const customMarkerLayer = L.layerGroup();
let customMarkerCount = 0;

function addCustomMarker(x, y, z) {
    const latlng = worldTolatLng(x, y);

    customMarkerCount++;
    const markerName = `Marker ${customMarkerCount}`;

    const markerIcon = L.divIcon({
        className: 'poiatlas',
        iconSize: [aSize, aSize],
        html: `<div style="background-color: #ff4444; border: 2px solid white; border-radius: 50%; width: 16px; height: 16px; box-shadow: 0 0 4px rgba(0,0,0,0.5);"></div>`
    });

    const marker = L.marker(latlng, { icon: markerIcon });

    const popupContent = `
        <b>${markerName}</b><br>
        X: ${x.toFixed(2)}<br>
        Y: ${y.toFixed(2)}<br>
        Z: ${z.toFixed(2)}<br>
        <button onclick="removeCustomMarker(${customMarkerCount})" style="margin-top:5px;cursor:pointer;">Remove</button>
    `;

    marker.bindPopup(popupContent);
    marker.customMarkerId = customMarkerCount;

    addGroupLayer('Custom Markers', 'Custom Markers');
    addToggleLayer('Custom Markers', markerName, marker);
    scheduleGroupedLayerControlUpdate();

    // Pan to the marker
    LeafletMap.setView(latlng, LeafletMap.getZoom());
}

function removeCustomMarker(markerId) {
    // Find and remove the marker from layers
    for (const [name, layer] of Object.entries(layerNames)) {
        if (layer.customMarkerId === markerId) {
            LeafletMap.removeLayer(layer);
            delete layerNames[name];
            break;
        }
    }
    scheduleGroupedLayerControlUpdate();
    LeafletMap.closePopup();
}

// Expose to global scope for popup button
window.removeCustomMarker = removeCustomMarker;

///////////////////////////////////////////////////

async function addPoi(areaId) {
    if (!currentArea) return;

    const response = await fetch('_content/Frontend/teleport_locations.txt');
    const text = await response.text();
    const lines = text.split('\n');

    const pois = lines.map(line => {
        const parts = line.split(' ');
        return {
            x: parseFloat(parts[1]),
            y: parseFloat(parts[2]),
            z: parseFloat(parts[3]),
            mapID: Number(parts[5]),
            name: parts.slice(6).join(' ').trim()
        };
    });

    const texture = getPixiIconTexture('poi');
    const targetArea = Zones[areaId];

    for (const poi of pois) {
        if (poi.mapID !== config.MapID) continue;
        if (!contains(targetArea, poi)) continue;

        const res = await getAreaIdAndZFromService(poi);
        const zone = getZone(res.areaId);
        if (!zone || zone.AreaID !== targetArea.AreaID) continue;

        const latlng = worldTolatLng(poi.x, poi.y);
        const worldPos = { x: poi.x, y: poi.y };

        const sprite = createSprite(latlng, texture);

        addSpriteClickHandler(sprite, async () => {
            await showPixiPopup(worldPos, latlng, poi.name, sprite);
        });

        pixiOverlay.utils.getContainer().addChild(sprite);

        const groupLayer = createPixiSpriteGroupLayer('POI', [sprite]);
        addToggleLayer('POI', poi.name, groupLayer, true);

        scheduleGroupedLayerControlUpdate();
    }

    schedulePixiRedraw();
}

async function addTeleportPoints() {
    const response = await fetch('_content/Frontend/teleport_locations.txt');
    const text = await response.text();
    const lines = text.split('\n');

    const texture = getPixiIconTexture('poi');

    for (const line of lines) {
        const parts = line.split(' ');
        if (parts.length < 7) continue;
        const x = parseFloat(parts[1]);
        const y = parseFloat(parts[2]);
        const z = parseFloat(parts[3]);
        const mapID = Number(parts[5]);
        const name = parts.slice(6).join(' ').trim();

        if (mapID !== config.MapID) continue;

        const latlng = worldTolatLng(x, y);
        const worldPos = { x, y, z };

        const sprite = createSprite(latlng, texture);
        addSpriteClickHandler(sprite, async () => {
            await showPixiPopup(worldPos, latlng, name, sprite);
        });

        pixiOverlay.utils.getContainer().addChild(sprite);
        const groupLayer = createPixiSpriteGroupLayer('Teleport', [sprite]);
        addToggleLayer('Teleport', `tp:${name}`, groupLayer, true);
    }

    scheduleGroupedLayerControlUpdate();
    schedulePixiRedraw();
}

async function addNpcSpawns(npcId, groupName = 'Spawn', iconName = 'red') {
    const spawns = spawnLocations[npcId];
    if (!spawns) return;

    // A spawn can outlive its creature entry: the two files are generated separately,
    // so an npcspawnlocations built from a newer dump than creatures.json references
    // ids that are simply absent. Unguarded, that threw out of the whole layer and the
    // zone rendered nothing - which reads as "the area json is broken" when it is not.
    const creature = creatures[npcId];
    if (!creature) {
        console.warn(`addNpcSpawns: no creature entry for ${npcId} - regenerate creatures.json`);
        return;
    }

    const npcName = creature.Name || npcId;
    const toggleControlName = `${npcName} - ${npcId} (${spawns.length})`;
    //var groupLayer = addGroupLayer(groupName, toggleControlName);

    const texture = getPixiIconTexture(eliteIcon(creature, iconName));
    const sprites = [];

    for (const worldPos of spawns) {
        const latlng = worldTolatLng(worldPos.x, worldPos.y);

        const sprite = createSprite(latlng, texture);

        const html = `
            ${npcName}
            <br/>${creature.MinLevel}-${creature.MaxLevel}
            <br/>${npcId}
            <br/><div onclick="openInNewTab('${baseUrl}/npc=${npcId}');">wowhead link</div>
        `;

        addSpriteClickHandler(sprite, async () => {
            await showPixiPopup(worldPos, latlng, html, sprite);
        });

        pixiOverlay.utils.getContainer().addChild(sprite);
        sprites.push(sprite);
    }

    const groupLayer = createPixiSpriteGroupLayer(groupName, sprites);
    addToggleLayer(groupName, toggleControlName, groupLayer, true);

    scheduleGroupedLayerControlUpdate();
}

async function addNodeTypeSpawns(oreName, coords, area, groupName) {
    const toggleControlName = `${oreName} (${coords.length})`;
    const texture = getPixiIconTexture(oreName);

    const sprites = [];

    for (const mapPos of coords) {
        const map = { x: mapPos[1], y: mapPos[0] };
        const worldPos = localToWorld(area, map);
        const latlng = worldTolatLng(worldPos.x, worldPos.y);

        const sprite = createSprite(latlng, texture);

        addSpriteClickHandler(sprite, async () => {
            await showPixiPopup(worldPos, latlng, oreName, sprite);
        });

        pixiOverlay.utils.getContainer().addChild(sprite);
        sprites.push(sprite);
    }

    const groupLayer = createPixiSpriteGroupLayer(groupName, sprites);
    addToggleLayer(groupName, toggleControlName, groupLayer, true);

    scheduleGroupedLayerControlUpdate();

    schedulePixiRedraw();
}


function openInNewTab(url) {
    window.open(url, '_blank').focus();
}

async function addSkinnableNpcsToArea(areaId) {
    const areaDBC = await getAreaOrCache(areaId);
    if (areaDBC == null) {
        return;
    }

    const areaName = Zones[areaId].AreaName;

    var count = 0;
    const firstRow = 8;

    for (const id of areaDBC.skinnable) {
        if (skinnableExclude[id] === true) continue;

        addNpcSpawns(id, `${areaName} Skinning`, Object.values(Circles)[++count % firstRow]);
    }
}

async function addNodeSpawnsToArea(areaId, nodeType) {
    const areaDBC = await getAreaOrCache(areaId);
    if (areaDBC == null) {
        return;
    }

    const area = Zones[areaId];
    const areaName = area.AreaName;

    // vein or herb
    const db = areaDBC[nodeType];

    for (const name in db) {
        const data = db[name][0];
        addNodeTypeSpawns(name, data.coords, area, `${areaName} ${nodeType}`);
    }
}

//////////////////////////////////////////////////

function npcLoc(npc) {
    return {
        x: npc.coords[0][1],
        y: npc.coords[0][0]
    }
}

const Circles = [
    "darkblue",
    "blue",
    "red",
    "yellow",
    "green",
    "yred",
    "yyellow",
    "ygreen",
]

async function getAreaOrCache(areaId) {
    if (areaCache[areaId] != null) {
        return areaCache[areaId];
    }

    const response = await fetch(`/area/${areaId}.json`)
        .catch(e => console.error(e));

    return areaCache[areaId] = await response.json();
}

// Cache for mailbox data per map
const mailboxCache = {};

async function getMailboxLocations(mapId) {
    if (mailboxCache[mapId]) return mailboxCache[mapId];

    const url = `/mailboxlocations/${mapId}.json`;
    const response = await fetch(url);
    if (!response.ok) return mailboxCache[mapId] = [];
    return mailboxCache[mapId] = await response.json();
}

async function addMailboxes() {
    const groupName = 'Mailboxes';

    addGroupLayer(groupName, groupName);

    const locations = await getMailboxLocations(config.MapID);
    if (!locations || locations.length === 0) {
        console.log(`No mailbox locations found for map ${config.MapID}`);
        return;
    }

    const texture = getPixiIconTexture('mailbox');

    for (const loc of locations) {
        const latlng = worldTolatLng(loc.x, loc.y);
        const sprite = createSprite(latlng, texture);
        const worldPos = { x: loc.x, y: loc.y, z: loc.z };

        const popupHtml = `
            <b>Mailbox</b>
            <br>World: ${loc.x.toFixed(1)}, ${loc.y.toFixed(1)}, ${loc.z.toFixed(1)}
        `;

        addSpriteClickHandler(sprite, async () => {
            await showPixiPopup(worldPos, latlng, popupHtml, sprite);
        });

        pixiOverlay.utils.getContainer().addChild(sprite);

        const groupLayer = createPixiSpriteGroupLayer(groupName, [sprite]);
        addToggleLayer(groupName, `Mailbox ${loc.x.toFixed(0)}, ${loc.y.toFixed(0)}`, groupLayer, true);
    }

    scheduleGroupedLayerControlUpdate();
    schedulePixiRedraw();

    console.log(`Loaded ${locations.length} mailbox locations for map ${config.MapID}`);
}

async function addNpc(npcType, overrideAreaId = null, factionFilter = 'both') {
    const area = overrideAreaId ? Zones[overrideAreaId] : currentArea;
    if (!area) return;

    addGroupLayer(npcType, npcType);

    const flag = npcFlags[npcType];
    const matches = factionFilter !== 'both'
        ? getCreatureByFlagFiltered(flag, factionFilter)
        : getCreatureByFlag(flag, []);
    const hitboxArea = Zones[area.AreaID + AreaIDOffset] ?? area;
    const texture = getPixiIconTexture(npcType);

    for (const creature of matches) {
        const spawns = spawnLocations[creature.Entry];
        if (!spawns) continue;

        const worldPos = spawns[0];

        var contain = false
        for (const subzone of getSubZones(area.AreaID)) {
            if (contains(subzone, worldPos)) {
                contain = true
                break;
            }
        }

        if (!contain && !contains(hitboxArea, worldPos)) continue;

        const npcName = creature.Name || `${npcType} ${creature.Entry}`;
        const latlng = worldTolatLng(worldPos.x, worldPos.y);

        const sprite = createSprite(latlng, texture);

        const popupHtml = `
            ${npcName}
            <br>${creature.SubName ?? ''}
            <br><div onclick="openInNewTab('${baseUrl}/npc=${creature.Entry}')">wowhead link</div>
        `;

        addSpriteClickHandler(sprite, async () => {
            await showPixiPopup(worldPos, latlng, popupHtml, sprite);
        });

        pixiOverlay.utils.getContainer().addChild(sprite);

        const groupLayer = createPixiSpriteGroupLayer(npcType, [sprite]);
        addToggleLayer(npcType, `${npcName} - ${npcType}`, groupLayer, true);
    }

    scheduleGroupedLayerControlUpdate();
    schedulePixiRedraw();

    lastRenderArea = area;
}

async function addEliteNpcsToArea(areaId, factionFilter = 'both') {
    const area = areaId ? Zones[areaId] : currentArea;
    if (!area) return;

    const groupName = 'Elites';
    addGroupLayer(groupName, groupName);

    const hitboxArea = Zones[area.AreaID + AreaIDOffset] ?? area;

    for (const creature of Object.values(creatures)) {
        if (creature.Rank <= 0) continue;

        if (factionFilter !== 'both') {
            const friendGroup = factionTemplates[creature.Faction];
            if (friendGroup !== undefined) {
                if (factionFilter === 'alliance' && (friendGroup & 2) === 0) continue;
                if (factionFilter === 'horde' && (friendGroup & 4) === 0) continue;
            }
        }

        const spawns = spawnLocations[creature.Entry];
        if (!spawns) continue;

        const worldPos = spawns[0];

        var contain = false;
        for (const subzone of getSubZones(area.AreaID)) {
            if (contains(subzone, worldPos)) {
                contain = true;
                break;
            }
        }

        if (!contain && !contains(hitboxArea, worldPos)) continue;

        const npcName = creature.Name || `Elite ${creature.Entry}`;
        const latlng = worldTolatLng(worldPos.x, worldPos.y);

        const texture = getPixiIconTexture(eliteIcon(creature, 'Elite'));
        const sprite = createSprite(latlng, texture);

        const popupHtml = `
            ${npcName}
            <br>${creature.SubName ?? ''}
            <br>${creature.MinLevel}-${creature.MaxLevel}
            <br><div onclick="openInNewTab('${baseUrl}/npc=${creature.Entry}')">wowhead link</div>
        `;

        addSpriteClickHandler(sprite, async () => {
            await showPixiPopup(worldPos, latlng, popupHtml, sprite);
        });

        pixiOverlay.utils.getContainer().addChild(sprite);

        const groupLayer = createPixiSpriteGroupLayer(groupName, [sprite]);
        addToggleLayer(groupName, `${npcName} - Elite`, groupLayer, true);
    }

    scheduleGroupedLayerControlUpdate();
    schedulePixiRedraw();

    lastRenderArea = area;
}

