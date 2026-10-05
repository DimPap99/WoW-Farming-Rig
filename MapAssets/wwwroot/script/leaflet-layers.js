// The player marker and path recording, live paths, the icon atlas and sprite layers, and the grouped layer control.
// Part of the Leaflet map, loaded after the pieces before it; see scripts/leaflet-pieces.mjs.

function angleDifference(a, b) {
    const diff = Math.abs(a - b) % 360;
    return diff > 180 ? 360 - diff : diff;
}

function getAngleBetweenVectors(a, b) {
    const dot = a.x * b.x + a.y * b.y;
    const magA = Math.sqrt(a.x ** 2 + a.y ** 2);
    const magB = Math.sqrt(b.x ** 2 + b.y ** 2);
    const cosTheta = dot / (magA * magB);
    return Math.acos(Math.min(Math.max(cosTheta, -1), 1)) * (180 / Math.PI);
}

function createPlayer(latlng) {
    requestAnimationFrame(() => {
        const playerIcon = L.divIcon({
            className: 'poiatlas player-icon',
            iconSize: [aSize, aSize],
            html: atlasImgName('player')
        });
        playerLayer = new L.marker(latlng, { icon: playerIcon })
        playerLayer.addTo(LeafletMap);
    });
}

function setPlayerLocation(x, y, dir) {
    if (!LeafletMap) return;

    const latlng = worldTolatLng(x, y);
    LeafletMap.setView(latlng, LeafletMap.getZoom());

    if (playerLayer === undefined || playerLayer === null) {
        createPlayer(latlng);
        return;
    }

    playerLayer.setLatLng(latlng);

    dir = -dir * (180 / Math.PI);
    dir = dir % 360;

    const arrow = document.querySelector('.player-icon');
    if (arrow == null) {
        createPlayer(latlng);
        return;
    }

    arrow.style.transformOrigin = 'center center';

    const currentTransform = arrow.style.transform || "";
    const cleaned = currentTransform.replace(/rotate\([^)]*\)/, '').trim();
    arrow.style.transform = `${cleaned} rotate(${dir}deg)`.trim();

    if (recordPlayerPath == false) {
        return;
    }

    const polyline = editableLayers.getLayers().find(layer => layer.PathName === currentRecordPlayerPath);
    if (polyline == null) {
        return;
    }

    const latlngs = polyline.getLatLngs();
    if (latlngs.length == 0) {
        polyline.addLatLng(latlng);
        return;
    }

    const lastLatLng = latlngs[latlngs.length - 1];
    const lastWorldPos = latLngToWorld(lastLatLng);
    const currentWorldPos = latLngToWorld(latlng);

    const dx = currentWorldPos.x - lastWorldPos.x;
    const dy = currentWorldPos.y - lastWorldPos.y;
    const distance = Math.sqrt(dx ** 2 + dy ** 2);

    const MIN_DISTANCE = 40;
    const MIN_ANGLE_CHANGE = 5;

    // Directional change detection (optional vector-based)
    let directionChanged = false;

    if (latlngs.length >= 2) {
        const prevLatLng = latlngs[latlngs.length - 2];
        const prevWorld = latLngToWorld(prevLatLng);

        const lastVector = {
            x: lastWorldPos.x - prevWorld.x,
            y: lastWorldPos.y - prevWorld.y,
        };

        const currentVector = {
            x: currentWorldPos.x - lastWorldPos.x,
            y: currentWorldPos.y - lastWorldPos.y,
        };

        const angle = getAngleBetweenVectors(lastVector, currentVector);
        directionChanged = angle > MIN_ANGLE_CHANGE;
    }

    if (distance > MIN_DISTANCE || directionChanged) {
        polyline.addLatLng(latlng);
    }
}

function setRecord(state) {

    if (playerLayer == null) {
        console.warn("Player layer not found");
        return;
    }

    if (recordPlayerPath == false && state == true) {

        const latlng = playerLayer.getLatLng();
        const polyline = new L.Polyline([latlng], { color: 'red', weight: 2, opacity: 1, smoothFactor: 1 });

        currentRecordPlayerPath = getFilePathFileName();

        polyline.PathName = currentRecordPlayerPath
        polyline.groupName = 'Paths';
        polyline.addTo(editableLayers);

        polyline.editing.enable();
    }
    else if (recordPlayerPath == true && state == false) {

        const polyline = editableLayers.getLayers().find(layer => layer.PathName === currentRecordPlayerPath);
        if (polyline) {
            polyline.editing.disable();
            editableLayers.removeLayer(polyline);

            currentRecordPlayerPath = '';

            addGroupLayer('Paths', 'Paths');
            assignLayer(polyline.groupName, polyline.PathName, polyline, true);
            bindRightClick(polyline, polyline.groupName, polyline.PathName);
            flashLayer(polyline);

            scheduleGroupedLayerControlUpdate();
        }
    }

    recordPlayerPath = state;
}


function isMap(p) {
    return p.x > 0 && p.x < 100;
}

function setPolyPath(name, path, force = false) {
    if (!LeafletMap) return;

    let polyline = layerNames[name];

    path = JSON.parse(path);

    var worlds = path;
    if (path.length > 0 && isMap(path[0])) {
        worlds = path.map(p => localToWorld(currentArea, flipXYLower(p)));
    }

    const latlngs = worlds.map(w => worldTolatLng(w.x, w.y));

    if (polyline == null) {
        polyline = new L.Polyline([latlngs], { color: name === 'Route' ? 'white' : 'blue', weight: 2, opacity: 1, smoothFactor: 1 });
        polyline.PathName = name;
        polyline.groupName = 'Navigation';
        polyline.addTo(editableLayers);

        addGroupLayer('Navigation', name);
        assignLayer(polyline.groupName, polyline.PathName, polyline, true);

        polyline.bringToFront();

        schedulePixiRedraw();
        scheduleGroupedLayerControlUpdate();
    }
    else {
        // The length comparison is an optimization for the live route, which only ever
        // shrinks as waypoints are consumed. It silently drops any update that happens to
        // keep the same point count - so re-rolling a generated route of N stops never
        // redrew, and switching to a different query with the same N left the old line on
        // the map. Callers that replace a path wholesale pass force.
        if (force || polyline.getLatLngs().length != latlngs.length) {
            polyline.setLatLngs(latlngs);

            polyline.bringToFront();
        }
    }
}

/// Removes a polyline previously added by setPolyPath, so a page can clear what it drew
/// instead of leaving a stale line behind.
function clearPolyPath(name) {
    const polyline = layerNames[name];
    if (!polyline) return;

    editableLayers.removeLayer(polyline);
    delete layerNames[name];

    schedulePixiRedraw();
    scheduleGroupedLayerControlUpdate();
}

function atlasImg(p) {
    return `<div class="poiatlas" style="background-position: ${p.x}px ${p.y}px;height:${aSize}px;">&nbsp</div>`
}

function eliteIcon(creature, fallbackIcon) {
    return creature.Rank == 4 ? 'RareElite' : creature.Rank > 0 ? 'Elite' : fallbackIcon;
}

function atlasImgName(name) {
    let iconPos = IconAtlas[name];

    if (iconPos == null) {
        console.warn(`Icon "${name}" not found in atlas using fallback icon.`);
        iconPos = IconAtlas['redcross'];
    }

    const leftPx = -(iconPos[0] * aSize);
    const topPx = -(iconPos[1] * aSize);
    return atlasImg({ x: leftPx, y: topPx });
}

/// test
function testdrawIconAtlasPixi() {
    const itemsPerRow = 16;
    const spacing = 0.1;

    let i = 0;
    for (const [name, [col, row]] of Object.entries(IconAtlas)) {
        const gridX = i % itemsPerRow;
        const gridY = Math.floor(i / itemsPerRow);

        const lat = -25 - (gridY * spacing);
        const lng = 25 + (gridX * spacing);
        const latlng = L.latLng(lat, lng);


        const texture = getPixiIconTexture(name);
        const sprite = createSprite(latlng, texture);

        addSpriteClickHandler(sprite, () => {
            const popup = L.popup({
                autoClose: false,
                closeOnClick: false,
                closeButton: true,
                className: 'pixi-tooltip'
            })
                .setLatLng(latlng)
                .setContent(name)
                .openOn(LeafletMap);
        });

        pixiOverlay.utils.getContainer().addChild(sprite);
        i++;
    }

    schedulePixiRedraw();
}


/// test

const atlasImagePath = '_content/MapAssets/img/atlas.png';
var baseAtlasTexture;

const atlasImage = new Image();
atlasImage.src = atlasImagePath;
atlasImage.onload = () => {
    baseAtlasTexture = PIXI.BaseTexture.from(atlasImage);
};


function getPixiIconTexture(name) {
    let iconPos = IconAtlas[name];

    if (!iconPos) {
        console.warn(`Icon "${name}" not found in atlas. Falling back to redcross.`);
        iconPos = IconAtlas['redcross'];
    }

    const frame = new PIXI.Rectangle(
        iconPos[0] * aSize,
        iconPos[1] * aSize,
        aSize,
        aSize
    );

    return new PIXI.Texture(baseAtlasTexture, frame);
}

function createSprite(latlng, texture) {
    const sprite = new PIXI.Sprite(texture);
    sprite.anchor.set(0.5);
    sprite.latlng = latlng;
    sprite.interactive = true;
    sprite.buttonMode = true;
    sprite.scale.set(1);

    return sprite;
}

// Helper function to properly add click handlers to sprites
function addSpriteClickHandler(sprite, handler) {
    sprite.interactive = true;
    sprite.buttonMode = true;
    sprite._leafletClickHandler = handler;
    clickableSprites.add(sprite);

    sprite.on('pointertap', (e) => {
        handler(e);
    });

    sprite.on('pointerover', (e) => {
    });
}


let controlUpdateScheduled = false;
function scheduleGroupedLayerControlUpdate() {
    if (controlUpdateScheduled) return;
    controlUpdateScheduled = true;
    requestAnimationFrame(() => {
        createGroupedLayerControl();
        controlUpdateScheduled = false;
    });
}

const layerGroupIcons = {
    'vendor': 'vendor', 'vendorammo': 'vendorammo', 'vendorfood': 'vendorfood',
    'vendorpoison': 'vendorpoison', 'vendorreagent': 'vendorreagent',
    'repair': 'repair', 'classtrainer': 'classtrainer',
    'professiontrainer': 'professiontrainer', 'flightmaster': 'flightmaster',
    'innkeeper': 'innkeeper', 'spirithealer': 'spirithealer',
    'stablemaster': 'stablemaster', 'Elites': 'Elite',
    'Search': 'redcross', 'POI': 'poi', 'Mailboxes': 'mailbox', 'Teleport': 'poi'
};

function getLayerGroupIcon(gName) {
    if (layerGroupIcons[gName]) return layerGroupIcons[gName];
    if (gName.includes('Skinning')) return 'skinnable';
    if (gName.includes('vein')) return 'mining';
    if (gName.includes('herb')) return 'Silverleaf';
    return null;
}

function applyLayerToggleIcon(control, gName) {
    const container = control.getContainer();
    if (!container) return;

    const toggle = container.querySelector('.leaflet-control-layers-toggle');
    if (!toggle) return;

    const iconName = getLayerGroupIcon(gName);
    if (!iconName) return;

    const iconPos = IconAtlas[iconName];
    if (!iconPos) return;

    const leftPx = -(iconPos[0] * aSize);
    const topPx = -(iconPos[1] * aSize);

    toggle.style.backgroundImage = `url('${atlasImagePath}')`;
    toggle.style.backgroundPosition = `${leftPx}px ${topPx}px`;
    toggle.style.backgroundSize = '512px auto';
    toggle.style.imageRendering = 'pixelated';
    toggle.style.width = `${aSize}px`;
    toggle.style.height = `${aSize}px`;
}

function createGroupedLayerControl() {
    const newGroupedControls = [];

    for (const gName in groupedOverlays) {
        const group = groupedOverlays[gName];
        if (!group || Object.keys(group).length === 0) continue;

        const sortedGroup = {};
        Object.keys(group)
            .sort((a, b) => a.localeCompare(b))
            .forEach(name => {
                sortedGroup[name] = group[name];
            });

        // Replace any existing control for this group
        const existing = groupedLayerControls.find(c => c._groupName === gName);
        if (existing) {
            LeafletMap.removeControl(existing);
        }

        const control = L.control.groupedLayers(null, { [gName]: sortedGroup }, { groupCheckboxes: true });
        control._groupName = gName;

        control.addTo(LeafletMap);

        // Move layer control into the sidebar layers column
        if (sidebarLayersColumn) {
            const container = control.getContainer();
            if (container) {
                sidebarLayersColumn.appendChild(container);
            }
        }

        requestAnimationFrame(() => {
            addZoomButtonsToLayerControl(control);
            addCloseButtonToGroupLayers(control);
            applyLayerToggleIcon(control, gName);
        });

        newGroupedControls.push(control);
    }

    for (const ctrl of groupedLayerControls) {
        if (!newGroupedControls.includes(ctrl)) {
            LeafletMap.removeControl(ctrl);
        }
    }

    groupedLayerControls.length = 0;
    groupedLayerControls.push(...newGroupedControls);
}




function addZoomButtonsToLayerControl(control) {
    const container = control.getContainer();
    const inputs = container.querySelectorAll('input.leaflet-control-layers-selector');

    inputs.forEach(input => {
        const label = input.closest('label');
        if (!label) return;

        if (label.querySelector('.zoom-to-btn')) return;

        const layerName = label.textContent.trim();

        label.style.position = 'relative';

        const btn = document.createElement('button');
        btn.textContent = '🔍';
        btn.className = 'zoom-to-btn';

        btn.style.position = 'absolute';
        btn.style.right = '4px';
        btn.style.top = '50%';
        btn.style.transform = 'translateY(-50%)';

        btn.style.cursor = 'pointer';
        btn.style.border = 'none';
        btn.style.background = 'transparent';
        btn.style.padding = '0';
        btn.style.fontSize = '14px';
        btn.title = 'Zoom to layer';

        btn.onclick = (e) => {
            e.preventDefault();
            e.stopPropagation();

            const layer = layerNames[layerName]; // or wherever you store the layers
            if (!layer) {
                return;
            }

            const zoom = LeafletMap.getZoom();

            if (layer.getLatLng && typeof layer.getLatLng === 'function') {
                LeafletMap.setView(layer.getLatLng(), zoom);
            }
            else if (layer.getBounds && typeof layer.getBounds === 'function') {
                LeafletMap.fitBounds(layer.getBounds(), { padding: [20, 20] });
            } else if (layer instanceof L.LayerGroup) {

                let found = false;
                const layerGroup = layer.getLayers ? layer.getLayers() : layer._layers || [];

                for (const sub of layerGroup) {
                    if (sub.getBounds instanceof Function) {
                        LeafletMap.fitBounds(sub.getBounds(), { padding: [20, 20] });
                        found = true;
                        break;
                    } else if (sub.getLatLng instanceof Function) {
                        LeafletMap.setView(sub.getLatLng(), zoom);
                        found = true;
                        break;
                    }
                }

                if (!found) {
                    console.warn("No zoomable sublayers found.");
                }
            } else {
                console.warn("Layer does not support zooming");
            }
        };

        label.appendChild(btn);
    });
}

// ❌ Group remove buttons
function addCloseButtonToGroupLayers(control) {
    const container = control.getContainer();

    const groupLabels = container.querySelectorAll('.leaflet-control-layers-group-label');
    groupLabels.forEach(span => {
        if (span.querySelector('.remove-group-btn')) return;

        span.style.position = 'relative';
        span.style.paddingRight = '20px'; // add space for button on the right

        const removeBtn = document.createElement('button');
        removeBtn.textContent = '❌';
        removeBtn.className = 'remove-group-btn';

        removeBtn.style.position = 'absolute';
        removeBtn.style.right = '6px';
        removeBtn.style.top = '50%';
        removeBtn.style.transform = 'translateY(-50%)';

        removeBtn.style.cursor = 'pointer';
        removeBtn.style.border = 'none';
        removeBtn.style.background = 'transparent';
        removeBtn.style.padding = '0';
        removeBtn.style.fontSize = '14px';
        removeBtn.style.color = 'red';

        const groupName = span.textContent.trim();
        removeBtn.title = `Remove group "${groupName}"`;

        removeBtn.onclick = (e) => {
            e.preventDefault();
            e.stopPropagation();

            if (!groupedOverlays[groupName]) return;

            //if (!confirm(`Remove all layers in group "${groupName}"?`)) return;

            const layers = groupedOverlays[groupName];
            for (const layerName in layers) {
                const layer = layers[layerName];
                if (LeafletMap.hasLayer(layer)) {
                    LeafletMap.removeLayer(layer);
                }

                // Properly cleanup PixiSpriteGroupLayer instances
                if (layer && typeof layer.destroy === 'function') {
                    layer.destroy();
                }

                if (layerNames[layerName]) {
                    delete layerNames[layerName];
                }
            }

            delete groupedOverlays[groupName];
            removeActivatedPOICategory(groupName);

            scheduleGroupedLayerControlUpdate();
        };

        span.appendChild(removeBtn);
    });
}

function setBoundToArea(areaId) {

    const area = Zones[areaId];
    if (area == null) {
        return;
    }

    currentArea = area;

    const bounds = getAreaBounds(area);

    const southWest = bounds[0];
    const northEast = bounds[1];
    const mapBounds = new L.LatLngBounds(southWest, northEast);

    LeafletMap.setMaxBounds(mapBounds);
}

function addToggleLayer(type, name, layer, visible = true) {
    if (layerNames[name] != null) {
        return;
    }

    if (savedLayerVisibility !== null && savedLayerVisibility.has(name)) {
        visible = savedLayerVisibility.get(name);
    }

    assignLayer(type, name, layer, visible);
}

function assignLayer(type, name, layer, visible) {

    groupedOverlays[type][name] = layer;
    layerNames[name] = layer;

    if (visible && !LeafletMap.hasLayer(layer)) {
        layer.addTo(LeafletMap);
    }
}

function addGroupLayer(type, name) {
    groupedOverlays[type] ??= {};
    return groupedOverlays[type][name] ??= new L.LayerGroup();
}

function getGroupLayer(type, name) {
    return groupedOverlays[type]?.[name] ?? null;
}

function createPixiSpriteGroupLayer(type, sprites) {
    groupedOverlays[type] ??= {};

    return new PixiSpriteGroupLayer(sprites);
}

const PixiSpriteGroupLayer = L.Layer.extend({
    initialize(sprites) {
        this._sprites = sprites;
        this._visible = true;
    },

    onAdd(map) {
        this._visible = true;
        for (const sprite of this._sprites) {
            sprite.visible = true;
        }
        schedulePixiRedraw();
    },

    onRemove(map) {
        this._visible = false;
        for (const sprite of this._sprites) {
            sprite.visible = false;
        }
        schedulePixiRedraw();
    },

    destroy() {
        for (const sprite of this._sprites) {
            clickableSprites.delete(sprite);
        }
    },

    getBounds: function () {
        const latlngs = this._sprites.map(s => s.latlng);
        return L.latLngBounds(latlngs);
    },

    getLatLng: function () {
        return this._sprites[0].latlng;
    }
});



async function load() {

    if (!enableUrlEdit) { return; }

    //addGroupLayer('SubZones', 'SubZones');
    //addSubZones();

    //await loadPathsByAreaNames();
    //await loadPathsByRaceNames();
}

async function loadPathsByAreaNames() {
    for (const area of Object.values(Zones)) {
        if (area.AreaID > AreaIDOffset) {
            continue;
        }

        await loadMapPathByFilter(area.AreaName);
    }
}

async function loadPathsByRaceNames() {
    for (const name of Object.keys(raceNames)) {

        await loadMapPathByFilter(name);
    }
}

