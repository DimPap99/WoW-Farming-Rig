// Clicks and coordinates: the page URL, sprite and map clicks, world/map/screen conversions, saving drawn paths, the zone at a point, and the ADT grid.
// Part of the Leaflet map, loaded after the pieces before it; see scripts/leaflet-pieces.mjs.

function synchronizeTitleAndURL() {
    const latlng = LeafletMap.getCenter();
    const zoom = LeafletMap.getZoom();

    const current =
    {
        Zoom: zoom,
        LatLng: latlng
    };

    const title = "Leaflet"

    const url = '/Leaflet/' + expansion + '/' + continent + '/' + zoom + '/' + latlng.lat.toFixed(3) + '/' + latlng.lng.toFixed(3) + '/';

    window.history.replaceState(current, title, url);

    document.title = title;
}

//// layers

function setText(id, text) {
    const el = document.getElementById(id);
    if (el) el.textContent = text;
}

function handleSpriteClick(e) {
    if (!LeafletMap || clickableSprites.size === 0) return false;

    const clickPoint = LeafletMap.latLngToContainerPoint(e.latlng);
    const hitRadius = aSize / 2;
    const hitRadiusSq = hitRadius * hitRadius;

    for (const sprite of clickableSprites) {
        if (!sprite.visible) continue;

        const spritePoint = LeafletMap.latLngToContainerPoint(sprite.latlng);
        const dx = clickPoint.x - spritePoint.x;
        const dy = clickPoint.y - spritePoint.y;

        if (dx * dx + dy * dy <= hitRadiusSq) {
            sprite._leafletClickHandler();
            return true;
        }
    }

    return false;
}

async function processOffsetClick(e) {

    const layerPoint = LeafletMap.project(e.latlng, config.maxZoom);

    const adt = screenToAdt(layerPoint);
    const worldPos = screenToWorld(layerPoint);

    const response = enableUrlEdit ? await getAreaIdAndZFromService(worldPos) : { areaId: 0, z: 0 };
    const areaId = response.areaId;
    const zPos = response.z.toFixed(0);

    //addGroupLayer('SubZones', 'SubZones');
    //addSubZones(areaId);
    //scheduleGroupedLayerControlUpdate();

    const map = worldToPercentage(worldPos, areaId);

    const adtClick = document.getElementById("adtClick");
    if (adtClick != null)
        adtClick.innerHTML = continent + '_' + adt.x + '_' + adt.y;

    const worldClick = document.getElementById("worldClick")
    if (worldClick != null)
        worldClick.innerHTML = worldPos.x.toFixed(2) + ' ' + worldPos.y.toFixed(2) + ' ' + zPos + ' ' + config.MapID;

    if (map != null) {
        currentArea = map.area;
        updateSidebarCurrentZone();

        const mapClick = document.getElementById("mapClick");
        if (mapClick != null)
            mapClick.innerHTML = map.p.x + ' ' + map.p.y;

        if (map.subZone != null) {
            const subZoneName = document.getElementById("subZoneName");

            const subName = map.subZone.AreaName != map.name ? map.subZone.AreaName : '';
            if (subZoneName != null)
                subZoneName.innerHTML = map.AreaID + ' ' + map.name + ' ' + subName;
        }
    }
}

///////////////////////////////////

function worldTolatLng(x, y) {
    const pxPerCoord = adtSize / blpSize;

    const offset = config.offset.min;

    const offsetX = (offset.y * adtSize) / pxPerCoord;
    const offsetY = (offset.x * adtSize) / pxPerCoord;

    const tx = y * -1;
    const xx = (mapSize / 2 + tx) / pxPerCoord - offsetX;

    const ty = x * -1;
    const yy = (mapSize / 2 + ty) / pxPerCoord - offsetY;

    return LeafletMap.unproject([xx, yy], config.maxZoom);
}

function latLngToWorld(latlng) {
    const pxPerCoord = adtSize / blpSize;

    const offset = config.offset.min;
    const offsetX = (offset.y * adtSize) / pxPerCoord;
    const offsetY = (offset.x * adtSize) / pxPerCoord;

    const point = LeafletMap.project(latlng, config.maxZoom);

    // Reverse xx and yy logic
    const xx = (point.x + offsetX) * pxPerCoord - mapSize / 2;
    const yy = (point.y + offsetY) * pxPerCoord - mapSize / 2;

    // Undo the earlier inversion
    const x = -yy;
    const y = -xx;

    return { x, y };
}

function screenToWorld(point) {
    const offset = config.offset.min;
    const tileSize = blpSize;

    const adtCenterX = ((point.y / tileSize) + offset.x) - 32;
    const adtCenterY = ((point.x / tileSize) + offset.y) - 32;

    const worldX = -(adtCenterX * adtSize);
    const worldY = -(adtCenterY * adtSize);

    return new L.Point(worldX, worldY);
}

function screenToAdt(point) {
    const offset = config.offset.min;
    const tileSize = blpSize;

    const adtX = Math.floor((point.x / tileSize) + offset.y);
    const adtY = Math.floor((point.y / tileSize) + offset.x);

    return new L.Point(adtX, adtY);
}

function worldToPercentage(p, areaId) {
    let bestParentArea = null;

    // Exact WorldMapArea hit (areaId is a zone that has its own map).
    if (Zones[areaId] != null) {
        bestParentArea = Zones[areaId];
    }
    else {
        // WMA subzone link - only present in data that fills ParentAreaId.
        const wmaSub = SubZones[areaId];
        if (wmaSub != null && Zones[wmaSub.ParentAreaId] != null) {
            bestParentArea = Zones[wmaSub.ParentAreaId];
        }
        else {
            // Walk the areaId parent chain up to a zone that has its own map.
            // This is what resolves TBC/WotLK terrain subzones (e.g. 4254 ->
            // Borean). Source is AreaTableById, itself built from the subzone
            // rows in WorldMapArea.
            let cur = areaId, guard = 0;
            while (cur && guard++ < 32) {
                if (Zones[cur] != null) {
                    bestParentArea = Zones[cur];
                    break;
                }
                const at = AreaTableById[cur];
                if (at == null) {
                    break;
                }
                cur = at.ParentAreaId;
            }
        }
    }

    // Last resort: the zone whose world Loc bounds contain the point
    // (game-file-free, WorldMapArea data only).
    if (!bestParentArea) {
        bestParentArea = findZoneByWorld(p.x, p.y);
    }

    if (!bestParentArea) {
        return { p: new L.Point(0, 0), name: "not found", subZoneName: '' };
    }

    // Subzone label: prefer the fine-grained name for the clicked areaId,
    // then any WMA subzone, else the zone itself.
    const leaf = AreaTableById[areaId];
    const bestSubZone = leaf != null
        ? { AreaID: leaf.AreaID, AreaName: leaf.AreaName, ParentAreaId: leaf.ParentAreaId }
        : (SubZones[areaId] ?? bestParentArea);

    return {
        p: new L.Point(toMapY(bestParentArea, p.y), toMapX(bestParentArea, p.x)),
        area: bestParentArea,
        AreaID: bestParentArea.AreaID,
        name: bestParentArea.AreaName,
        subZone: bestSubZone
    };
}

const getAreaIdFromServiceCache = {};

async function getAreaIdAndZFromService(worldPos) {

    const key = `${worldPos.x.toFixed(2)}:${worldPos.y.toFixed(2)}`;

    if (key in getAreaIdFromServiceCache) {
        return getAreaIdFromServiceCache[key];
    }

    const result = await pathingFetch(`/area?mapId=${config.MapID}&x=${worldPos.x}&y=${worldPos.y}`);
    if (!result.ok) {
        // Not cached: the service may come back.
        return { areaId: 0, z: 0 };
    }

    return getAreaIdFromServiceCache[key] = result.body;
}

// Saves zone map points ({ x, y, z }) as the recorded path `fileName`, a library name.
async function savePath(fileName, mapPoints) {
    const result = await pathingFetch(`/paths/${pathNameUrl(fileName)}`, pathingJson('PUT', mapPoints));
    if (!result.ok) {
        mapNotice(`Saving ${fileName} failed: ${result.error}`);
        throw new Error(`saving ${fileName} failed: ${result.error}`);
    }
}

function localToWorld(area, point) {
    const x = toWorldX(area, point.x);
    const y = toWorldY(area, point.y);

    return new L.Point(x, y);
}

function toMapX(area, value) {
    return (100 - (((value - area.LocBottom) * 100) / (area.LocTop - area.LocBottom))).toFixed(2);
}

function toMapY(area, value) {
    return (100 - (((value - area.LocRight) * 100) / (area.LocLeft - area.LocRight))).toFixed(2);
}

function toWorldX(area, value) {
    return ((area.LocBottom - area.LocTop) * value / 100) + area.LocTop;
}

function toWorldY(area, value) {
    return ((area.LocRight - area.LocLeft) * value / 100) + area.LocLeft;
}

function contains(area, point) {
    const minX = Math.min(area.LocTop, area.LocBottom);
    const maxX = Math.max(area.LocTop, area.LocBottom);
    const minY = Math.min(area.LocLeft, area.LocRight);
    const maxY = Math.max(area.LocLeft, area.LocRight);

    return point.x >= minX && point.x <= maxX && point.y >= minY && point.y <= maxY;
}

// Smallest (most specific) WorldMapArea zone whose Loc bounds contain the world
// point. Used when a click's areaId has no WorldMapArea row (common in
// Outland/Northrend), so the map still shows local coords + zone name.
function findZoneByWorld(x, y) {
    const point = { x, y };
    let best = null;
    let bestArea = Infinity;

    for (const zone of Object.values(Zones)) {
        if (!contains(zone, point)) {
            continue;
        }

        const area = Math.abs(zone.LocTop - zone.LocBottom) * Math.abs(zone.LocLeft - zone.LocRight);
        if (area < bestArea) {
            bestArea = area;
            best = zone;
        }
    }

    return best;
}

//////////////////////////////////////////////////////////////
//////////////////// ADT layer on off //////////////////////////////////////////
///////////////////////////////////////////////////////////////////////

function addADT() {

    ADTEnabled = true;

    ADTGridLayer.clearLayers();

    const debugCell = false;
    const subDiv = 16;
    const subSize = adtSize / subDiv;

    for (let x = 0; x < 64; x++) {
        for (let y = 0; y < 64; y++) {
            // ADT-level borders (red)
            let xStart = maxSize - (x * adtSize);
            let yStart = maxSize - (y * adtSize);

            let xEnd = xStart - adtSize;
            let yEnd = yStart - adtSize;

            // vertical ADT line
            ADTGridLayer.addLayer(new L.polyline([
                worldTolatLng(xStart, -maxSize),
                worldTolatLng(xStart, maxSize)
            ], { weight: 0.1, color: 'red' }));

            // horizontal ADT line
            ADTGridLayer.addLayer(new L.polyline([
                worldTolatLng(maxSize, yStart),
                worldTolatLng(-maxSize, yStart)
            ], { weight: 0.1, color: 'red' }));

            // Now draw 16x16 subdivisions (gray)
            for (let i = 1; debugCell && i < subDiv; i++) {
                // vertical subdivision lines
                const subX = xStart - (i * subSize);
                ADTGridLayer.addLayer(new L.polyline([
                    worldTolatLng(subX, yStart),
                    worldTolatLng(subX, yEnd)
                ], { weight: 1, color: 'green' }));

                // horizontal subdivision lines
                const subY = yStart - (i * subSize);
                ADTGridLayer.addLayer(new L.polyline([
                    worldTolatLng(xStart, subY),
                    worldTolatLng(xEnd, subY)
                ], { weight: 1, color: 'green' }));
            }
        }
    }

    refreshADTGrid();

    if (!LeafletMap.hasLayer(ADTGridLayer)) {
        LeafletMap.addLayer(ADTGridLayer);
    }
}

function removeADT() {
    ADTEnabled = false;
    LeafletMap.removeLayer(ADTGridLayer);
    LeafletMap.removeLayer(ADTGridTextLayer);
}

function refreshADTGrid() {

    if (!ADTEnabled) {
        return;
    }

    if (LeafletMap.getZoom() < 5) {
        if (LeafletMap.hasLayer(ADTGridTextLayer)) {
            ADTGridTextLayer.clearLayers();
        }
        return;
    }

    ADTGridTextLayer.clearLayers();

    for (let x = 0; x < 64; x++) {
        for (let y = 0; y < 64; y++) {
            const latlng = worldTolatLng(maxSize - (x * adtSize) - 25, maxSize - (y * adtSize) - 25);

            if (LeafletMap.getBounds().contains(latlng)) {
                const myIcon = L.divIcon({ className: 'adtcoordicon', html: '<div class="adtText">' + y + '_' + x + '</div>' });
                ADTGridTextLayer.addLayer(new L.marker(latlng, { icon: myIcon }));
            }
        }
    }

    // Ensure the layer is added after processing
    if (!LeafletMap.hasLayer(ADTGridTextLayer)) {
        LeafletMap.addLayer(ADTGridTextLayer);
    }
}

function createLeafletButtonControl({ className = '', iconHTML = '', setImageFn = null, onClick = null, npcType = null }) {
    return L.Control.extend({
        options: {
            position: 'topleft',
            npcType: npcType
        },

        initialize: function (npcType, options) {
            L.Util.setOptions(this, options);
            this.options.npcType = npcType ?? this.options.npcType;
        },

        onAdd: function (map) {
            const container = L.DomUtil.create('div', `leaflet-bar leaflet-control leaflet-control-custom ${className}`);

            if (setImageFn && this.options.npcType) {
                setImageFn(container.style, this.options.npcType);
            }

            if (iconHTML) {
                container.innerHTML = iconHTML;
            }

            container.style.backgroundColor = 'white';
            container.style.width = '30px';
            container.style.height = '30px';
            container.style.display = 'flex';
            container.style.alignItems = 'center';
            container.style.justifyContent = 'center';

            L.DomEvent.disableClickPropagation(container);

            container.onclick = () => {
                if (typeof onClick === 'function') {
                    onClick(this.options.npcType, map);
                }
            };

            return container;
        }
    });
}

