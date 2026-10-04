// The map's data and lifecycle: the DBC lookups and NPC search, popups, creating and disposing the map, and area changes.
// Part of the Leaflet map, loaded after the pieces before it; see scripts/leaflet-pieces.mjs.

async function getDBC(database) {
    const url = `/dbc/${database}.json`;

    // no-cache: always revalidate. DBC files are served per-client (DataConfig.Exp);
    // switching clients (e.g. vanilla -> TBC) reuses the same URL, so a cached copy
    // would otherwise serve the wrong expansion's data (missing Outland/Northrend).
    return fetch(url, { cache: 'no-cache' })
        .then(r => r.ok ? r.json() : null)
        .catch(e => console.error(e));
}

async function getNpcSpawnLocations(mapId) {
    const url = `/npcspawnlocations/${mapId}.json`;

    return fetch(url, { cache: 'no-cache' })
        .then(r => r.ok ? r.json() : null)
        .catch(e => console.error(e));
}

function filterContientsAndInvalid(db, mapID) {
    return db.filter(x => x.MapID == mapID &&
        x.AreaName != "Eastern Kingdoms" &&
        x.AreaName != "Azeroth" &&
        x.AreaName != "Kalimdor" &&
        x.AreaName != "Hyjal" &&
        x.AreaName != "Outland"
    );
}

function createZoneLookup(db) {
    const lookup = {};
    for (const area of db) {
        if (area.ParentAreaId === 0) {
            lookup[area.AreaID] = area;
        }
    }
    return lookup;
}

function createSubZoneLookup(db) {
    const lookup = {};
    for (const subArea of db) {
        if (subArea.ParentAreaId !== 0) {
            lookup[subArea.AreaID] = subArea;
        }
    }
    return lookup;
}

function getZone(areaId) {
    const subZone = SubZones[areaId];
    return subZone
        ? Zones[subZone.ParentAreaId] ?? null
        : Zones[areaId] ?? null;
}

function getSubZones(areaId) {
    const subZones = [];
    for (const subZone of Object.values(SubZones)) {
        if (subZone.ParentAreaId === areaId) {
            subZones.push(subZone);
        }
    }
    return subZones;
}

function createCreaturesLookup(db) {
    const lookup = {};
    for (const entry of db) {
        lookup[entry.Entry] = entry;
    }
    return lookup;
}

var npcFlags = {};

function getCreatureByFlag(flag, excludedFactions = []) {
    return Object.values(creatures).filter(c => {
        const hasFlag = (c.NpcFlag & flag) !== 0;
        const isExcluded = excludedFactions.includes(c.Faction);
        return hasFlag && !isExcluded;
    });
}

async function loadFactionTemplates() {
    const db = await getDBC("factiontemplates");
    if (!db) return;
    for (const entry of db) {
        factionTemplates[entry.Id] = entry.FriendGroup;
    }
}

function getCreatureByFlagFiltered(flag, factionFilter) {
    return getCreatureByFlag(flag, []).filter(c => {
        if (factionFilter === 'both') return true;
        const friendGroup = factionTemplates[c.Faction];
        if (friendGroup === undefined) return true;
        if (factionFilter === 'alliance') return (friendGroup & 2) !== 0;
        if (factionFilter === 'horde') return (friendGroup & 4) !== 0;
        return true;
    });
}

function searchCreaturesByName(query, factionFilter, zoneId, typeFamily = null) {
    const hasQuery = query && query.length >= 2;
    if (!hasQuery && !typeFamily) return [];

    const lowerQuery = hasQuery ? query.toLowerCase() : null;
    const results = [];
    const targetArea = zoneId ? Zones[zoneId] : null;
    const subZones = zoneId ? getSubZones(zoneId) : [];

    let filterKey = null;
    let filterValue = 0;
    if (typeFamily) {
        const parts = typeFamily.split(':');
        filterKey = parts[0];
        filterValue = parseInt(parts[1], 10);
    }

    for (const creature of Object.values(creatures)) {
        if (filterKey) {
            if (filterKey === 'type' && creature.Type !== filterValue) continue;
            if (filterKey === 'family' && creature.Family !== filterValue) continue;
        }

        if (lowerQuery) {
            if (!creature.Name || !creature.Name.toLowerCase().includes(lowerQuery)) continue;
        }

        if (factionFilter !== 'both') {
            const friendGroup = factionTemplates[creature.Faction];
            if (friendGroup !== undefined) {
                if (factionFilter === 'alliance' && (friendGroup & 2) === 0) continue;
                if (factionFilter === 'horde' && (friendGroup & 4) === 0) continue;
            }
        }

        if (targetArea) {
            const spawns = spawnLocations[creature.Entry];
            if (!spawns) continue;
            const worldPos = spawns[0];
            let inZone = contains(targetArea, worldPos);
            if (!inZone) {
                const hitboxArea = Zones[zoneId + AreaIDOffset];
                if (hitboxArea) inZone = contains(hitboxArea, worldPos);
            }
            if (!inZone) {
                for (const sub of subZones) {
                    if (contains(sub, worldPos)) { inZone = true; break; }
                }
            }
            if (!inZone) continue;
        }

        results.push(creature);
        if (results.length >= 50) break;
    }
    return results;
}

function copyClipboardOnClick(element) {
    element.onclick = (e) => {
        e.preventDefault();
        e.stopPropagation();

        const text = element.innerText;
        navigator.clipboard.writeText(text).then(() => {
            //console.log('Text copied to clipboard:', text);
            element.classList.add('flash');

            setTimeout(() => {
                element.classList.remove('flash');
            }, 300);

        }).catch(err => {
            console.error('Error copying text: ', err);
        });
    };
}

async function addCoordinates(worldPos) {
    const response = await getAreaIdAndZFromService(worldPos);
    const areaId = response.areaId;
    const zPos = worldPos.z ?? response.z;

    const map = worldToPercentage(worldPos, areaId);

    return `
    <br/><span class="copy-coords">${map.p.x} ${map.p.y}</span>
    <br/><span class="copy-coords">${worldPos.x.toFixed(2)} ${worldPos.y.toFixed(2)} ${zPos.toFixed(2)} ${config.MapID}</span>
    `;
}

function bindPopupEnrichCoordinates(marker, worldPos, text) {
    marker.on('click', async (e) => {
        const html = `${text}${await addCoordinates(worldPos)}`;
        marker.bindPopup(html).openPopup();

        setTimeout(() => {
            const popupEl = marker.getPopup().getElement();
            const spans = popupEl.querySelectorAll('.copy-coords');
            spans.forEach(span => {
                copyClipboardOnClick(span);
            });
        }, 10);
    });
}

async function showPixiPopup(worldPos, latlng, baseHtml, sprite = null) {
    // Prevent multiple popups on the same marker
    if (sprite && sprite.popup && LeafletMap.hasLayer(sprite.popup)) {
        sprite.popup.bringToFront();
        return;
    }

    const popupId = `popup-${Date.now()}-${Math.floor(Math.random() * 1000)}`;
    const loadingId = `${popupId}-loading`;

    const popup = L.popup({
        autoClose: false,
        closeOnClick: false,
        closeButton: true,
        className: 'pixi-tooltip'
    })
        .setLatLng(latlng)
        .setContent(`
            <div id="${popupId}">
                ${baseHtml}
                <br/><em id="${loadingId}">Loading coordinates...</em>
            </div>
        `)
        .openOn(LeafletMap);

    if (sprite) {
        sprite.popup = popup;
    }

    requestAnimationFrame(() => {
        const popupEl = document.getElementById(popupId);
        if (!popupEl) return;
        popupEl.querySelectorAll('.copy-coords').forEach(copyClipboardOnClick);
    });

    const enriched = await addCoordinates(worldPos);

    const popupEl = document.getElementById(popupId);
    if (popupEl) {
        const loadingEl = document.getElementById(loadingId);
        if (loadingEl) loadingEl.remove();
        popupEl.insertAdjacentHTML('beforeend', enriched);
        popupEl.querySelectorAll('.copy-coords').forEach(copyClipboardOnClick);
    }
}


function getFilePathFileName() {
    const currentAreaName = currentArea.AreaName;
    const currentDate = new Date().toISOString().replace(/[-:T]/g, '_').split('.')[0];
    return `${currentAreaName}_${currentDate}.json`;
}

function setBaseUrl(e) {
    switch (e) {
        case 'som':
            baseUrl = "https://classic.wowhead.com";
            break;
        case 'tbc':
            baseUrl = "https://tbc.wowhead.com";
            break;
        case 'wrath':
            baseUrl = "https://www.wowhead.com/wotlk";
            break;
        case 'cata':
            baseUrl = "https://www.wowhead.com/cata";
            break;
        default:
            baseUrl = "https://www.wowhead.com/";
            break;
    }
}

function initializeMap(x, y) {
    return new L.map('js-map', {
        center: [x, y],
        zoom: startZoom,
        minZoom: 2,
        maxZoom: maxZoom,
        crs: L.CRS.Simple,
        zoomControl: false,
        preferCanvas: true
    });
}

function disposeMap() {
    dotNetHelper = null;

    if (LeafletMap && Object.keys(layerNames).length > 0) {
        savedLayerVisibility = new Map();
        for (const name in layerNames) {
            savedLayerVisibility.set(name, LeafletMap.hasLayer(layerNames[name]));
        }
    }

    if (currentResizeObserver) {
        currentResizeObserver.disconnect();
        currentResizeObserver = null;
    }

    if (pixiOverlay) {
        if (LeafletMap && LeafletMap.hasLayer(pixiOverlay)) {
            pixiOverlay.remove();
        }
        pixiOverlay = null;
    }

    if (pixiContainer) {
        pixiContainer.removeChildren();
    }

    clickableSprites.clear();

    editableLayers.clearLayers();
    ADTGridLayer.clearLayers();
    ADTGridTextLayer.clearLayers();

    playerLayer = null;

    if (LeafletMap) {
        LeafletMap.off();
        LeafletMap.remove();
        LeafletMap = null;
    }

    for (const control of groupedLayerControls) {
        try { control.remove(); } catch (_) { }
    }
    groupedLayerControls = [];
    sidebarLayersColumn = null;

    groupedOverlays = {
        "Zones": {},
        "Paths": {},
    };

    layerNames = {};

    enableUrlEdit = false;
    ADTEnabled = false;
    recordPlayerPath = false;

    currentArea = undefined;
    lastRenderArea = undefined;
}

async function init(e, c, z, x, y, urlEdit, flags, dotNetRef) {

    npcFlags = flags;

    disposeMap();

    dotNetHelper = dotNetRef || null;

    expansion = e;
    enableUrlEdit = urlEdit;

    // Supported when the client's era has a config for this continent, either
    // its own or one inherited via the shared-art fallback (Northrend/Outland).
    if (!continentConfig(expansion, c)) {
        return;
    }

    setBaseUrl(expansion);

    continent = c;
    startZoom = z;

    config = continentConfig(expansion, continent);

    maxSize = Math.max(config.resX, config.resY);
    const multi = 17066.66666666667 / maxSize;
    maxSize = maxSize * multi;      //
    mapSize = maxSize * 2;          //34133,33333333333
    adtSize = mapSize / 64; 	    //533,3333333333333

    LeafletMap = initializeMap(x, y);

    if (!pixiContainer) {
        pixiContainer = new PIXI.Container();
    }
    pixiContainer.interactive = true; // important
    pixiContainer.interactiveChildren = true; // ensures its children (your sprites) are interactive

    let firstDraw = true;
    let prevZoom;

    //pixiContainer = new PIXI.Container();
    pixiOverlay = new L.PixiOverlay((utils) => {
        const zoom = utils.getMap().getZoom();
        const scaleFactor = utils.getMap().getZoomScale(6, zoom);
        const container = utils.getContainer();
        const renderer = utils.getRenderer();

        for (const sprite of container.children) {
            if (!sprite.visible) continue;

            const projected = utils.latLngToLayerPoint(sprite.latlng);
            sprite.x = projected.x;
            sprite.y = projected.y;

            if (sprite instanceof PIXI.Text) {
                const textZoomFactor = Math.min(1.5, Math.max(0.5, scaleFactor * 1.2));
                sprite.scale.set(textZoomFactor);
            } 
            if (sprite.interactive) {
                sprite.scale.set(aSize / sprite.texture.width * scaleFactor);
            }
            else {
                sprite.scale.set(aSize / sprite.texture.width * Math.min(scaleFactor, 3) * 3);
                // TODO: FIX bitmap rendered subzone texts as sprites
                //const textZoomFactor = Math.min(1.5, Math.max(0.5, scaleFactor * 1.2));
                //sprite.scale.set(textZoomFactor);
            }

            //if (sprite.interactive) {
            //    const size = aSize * scaleFactor;
            //    sprite.hitArea = new PIXI.Rectangle(-size/2, -size/2, size, size);
            //} 
        }


        if (firstDraw) {
            // set coordinates
        }

        if (firstDraw || prevZoom !== zoom) {
            // update by scale
        }

        firstDraw = false;
        prevZoom = zoom;
        renderer.render(pixiContainer);
    }, pixiContainer,
        {
            destroyInteractionManager: false,
            autoPreventDefault: false
        }
    );
    pixiOverlay.addTo(LeafletMap);

    const southWest = LeafletMap.unproject([0, config.resY], config.maxZoom);
    const northEast = LeafletMap.unproject([config.resX, 0], config.maxZoom);
    const mapBounds = new L.LatLngBounds(southWest, northEast);

    LeafletMap.setMaxBounds(mapBounds);

    LeafletMap.doubleClickZoom.disable();

    const tileBase = await resolveTileBase(continent);
    L.tileLayer(tileBase + '/' + continent + '/z{z}x{x}y{y}.webp', {
        maxZoom: maxZoom,
        maxNativeZoom: config.maxZoom,
        continuousWorld: true,
        tileSize: tileSize,
        bounds: mapBounds,
        zoomOffset: 0,
        noWrap: true
    }).addTo(LeafletMap);

    const wmaRaw = await getDBC("WorldMapArea");

    // areaId -> name + parent, built from the FULL response before the
    // per-continent filter below. WorldMapArea already carries subzone rows
    // (name, ParentAreaId and bounds, extended from the baked area grids by
    // ReadDBC_CSV's worldmap extractor), so it answers what AreaTable.json used
    // to and there is no reason to fetch a second file for the same data.
    AreaTableById = {};
    let subZoneRows = 0;
    if (Array.isArray(wmaRaw)) {
        for (const a of wmaRaw) {
            if (!a.AreaID) continue;          // continent/instance maps carry no areaId
            AreaTableById[a.AreaID] = a;
            if (!a.UIMapId) subZoneRows++;    // no UI map of its own => a subzone row
        }
    }

    // An era whose worldmaparea was generated before the subzone extension (or
    // without Json/subzones/<exp> present) has UI-map rows only, and then the
    // parent walk has nothing to climb. Fall back to AreaTable.json for those
    // rather than silently losing every subzone label.
    if (subZoneRows === 0) {
        const areaTableRaw = await getDBC("AreaTable");
        if (Array.isArray(areaTableRaw)) {
            for (const a of areaTableRaw) {
                AreaTableById[a.AreaID] = a;
            }
        }
    }

    WMADB = wmaRaw.sort((a, b) => a.AreaID - b.AreaID);
    WMADB = filterContientsAndInvalid(WMADB, config.MapID);
    Zones = createZoneLookup(WMADB);
    SubZones = createSubZoneLookup(WMADB);

    let creaturesFile = await getDBC("creatures");
    creatures = createCreaturesLookup(creaturesFile);

    spawnLocations = await getNpcSpawnLocations(config.MapID);

    await loadFactionTemplates();

    //setBoundToArea(12);

    await load();

    //editablePathLayerControl = L.control.layers(null, null, { collapsed: true, autoZIndex: false }).addTo(LeafletMap);

    if (enableUrlEdit) {
        LeafletMap.addLayer(editableLayers);
        LeafletMap.addControl(drawControl);
    }

    // Sidebar is always visible regardless of enableUrlEdit
    createSidebar().addTo(LeafletMap);

    if (typeof costZoneInit === 'function') {
        await costZoneInit(continent);
    }

    if (typeof navmeshCoverageInit === 'function') {
        await navmeshCoverageInit(continent);
    }

    if (enableUrlEdit && typeof bakeInit === 'function') {
        bakeInit(continent);
    }

    if (enableUrlEdit && typeof customPoiInit === 'function') {
        customPoiInit();
    }

    LeafletMap.on(L.Draw.Event.CREATED, function (e) {
        // Circles, rectangles and (in road mode) polylines are cost zones.
        if (costZoneHandleCreated(e)) {
            return;
        }

        var type = e.layerType;
        var layer = e.layer;

        layer.options.color = '#ff0000';

        // every drawn layer is added to the editableLayers
        editableLayers.addLayer(layer);
    });

    LeafletMap.on(L.Draw.Event.DELETED, function (e) {
        costZoneHandleDeleted(e.layers.getLayers());
    });

    LeafletMap.on(L.Draw.Event.EDITED, function (e) {
        const layers = e.layers.getLayers();

        if (costZoneHandleEdited(layers)) {
            return;
        }

        for (const layer of layers) {
            if (layer.groupName !== undefined && layer.PathName !== undefined) {
                editableLayers.removeLayer(layer);
                addGroupLayer('Paths', 'Paths');
                assignLayer(layer.groupName, layer.PathName, layer, true);

                flashLayer(layer);
            }
            else {

                // currently only polyline Path supported
                if (!(layer instanceof L.Polyline) || (layer instanceof L.Polygon)) {
                    console.warn("Layer is not a polyline or polygon");
                    continue;
                }

                editableLayers.removeLayer(layer);

                const path = getFilePathFileName();

                addGroupLayer('Paths', 'Paths');

                bindRightClick(layer, 'Paths', path);

                flashLayer(layer);

                addToggleLayer('Paths', layer.PathName, layer, true);
                scheduleGroupedLayerControlUpdate();
            }
        }
    });

    LeafletMap.on('moveend zoomend dragend', function () {

        if (enableUrlEdit) {
            synchronizeTitleAndURL();
        }

        refreshADTGrid();
    });

    LeafletMap.on('click', function (e) {
        if (!handleSpriteClick(e)) {
            processOffsetClick(e);
        }
    });


    const mapContainer = document.getElementById('js-map');

    currentResizeObserver = new ResizeObserver(() => {
        requestAnimationFrame(() => {
            if (!LeafletMap || !pixiOverlay) return;
            LeafletMap.invalidateSize();
            pixiOverlay.redraw();
        })
    });

    currentResizeObserver.observe(mapContainer);

    const adtClick = document.getElementById("adtClick");
    if (adtClick != null)
        copyClipboardOnClick(adtClick);

    const worldClick = document.getElementById("worldClick");
    if (worldClick != null)
        copyClipboardOnClick(worldClick);

    const mapClick = document.getElementById("mapClick");
    if (mapClick != null)
        copyClipboardOnClick(mapClick);

    scheduleGroupedLayerControlUpdate();

    //testdrawIconAtlasPixi();
}

async function updateArea(areaId) {
    if (!LeafletMap) return;

    const area = Zones[areaId];
    if (area == null) {
        return;
    }

    if (currentArea != area) {
        setBoundToArea(areaId);

        if (enableUrlEdit) {
            await loadMapPathByFilter(area.AreaName);
            requestAnimationFrame(() => {
                scheduleGroupedLayerControlUpdate();
            });
        }
    }

    currentArea = area;
    updateSidebarCurrentZone();

    await replayActivatedPOICategories();
}

async function replayActivatedPOICategories() {
    if (activatedPOICategories.size === 0) return;

    const zoneId = getSidebarZoneId();
    const factionFilter = getSidebarFactionFilter();
    if (!zoneId && !currentArea) return;
    const effectiveZoneId = zoneId ?? currentArea?.AreaID;

    for (const category of activatedPOICategories) {
        if (category.startsWith('npc:')) {
            await addNpc(category.substring(4), effectiveZoneId, factionFilter);
        } else if (category === 'skinnable') {
            await addSkinnableNpcsToArea(effectiveZoneId);
        } else if (category === 'node:vein') {
            await addNodeSpawnsToArea(effectiveZoneId, 'vein');
        } else if (category === 'node:herb') {
            await addNodeSpawnsToArea(effectiveZoneId, 'herb');
        } else if (category === 'mailbox') {
            await addMailboxes();
        } else if (category === 'elites') {
            await addEliteNpcsToArea(effectiveZoneId, factionFilter);
        }
    }

    scheduleGroupedLayerControlUpdate();
}

function removeActivatedPOICategory(groupName) {
    if (activatedPOICategories.delete(`npc:${groupName}`)) return;

    if (groupName === 'Elites') {
        activatedPOICategories.delete('elites');
        return;
    }

    const mapping = { 'Mailboxes': 'mailbox' };
    if (mapping[groupName]) {
        activatedPOICategories.delete(mapping[groupName]);
        return;
    }

    if (groupName.endsWith(' Skinning')) activatedPOICategories.delete('skinnable');
    else if (groupName.endsWith(' vein')) activatedPOICategories.delete('node:vein');
    else if (groupName.endsWith(' herb')) activatedPOICategories.delete('node:herb');
}

