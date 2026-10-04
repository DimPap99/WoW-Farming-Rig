// Zone outlines and labels, route files drawn on the map, and the draw and edit tools with their right-click menu.
// Part of the Leaflet map, loaded after the pieces before it; see scripts/leaflet-pieces.mjs.

function setImage(style, npcType) {

    const iconPos = IconAtlas[npcType];
    const leftPx = -(iconPos[0] * aSize);
    const topPx = -(iconPos[1] * aSize);

    style.backgroundPosition = `${leftPx}px ${topPx}px`;
    style.width = `${aSize}px`;
    style.height = `${aSize}px`;
}

//////////////////////////////////////////////////

function getAreaBounds(area) {
    return [
        [worldTolatLng(area.LocBottom, area.LocLeft)],
        [worldTolatLng(area.LocTop, area.LocRight)]
    ];
}



function addZones() {

    addGroupLayer('Zones', 'Zones');

    for (const area of Object.values(Zones)) {
        if (area.AreaID < AreaIDOffset) {
            continue;
        }

        if (area.AreaID == 0) {
            console.error(area);
            continue;
        }

        const bounds = getAreaBounds(area);
        const color = getRandomColor();

        const rect = L.rectangle(bounds, { color: color, weight: 0.5 });

        const originalArea = Zones[area.AreaID - AreaIDOffset];
        if (originalArea == null) {
            console.error(area);
            continue;
        }
        addToggleLayer('Zones', originalArea.AreaName, rect, false);
    }
}

function addSubZones(areaId) {

    //const visible = areaId != null;

    for (const area of Object.values(SubZones)
        .filter(area => areaId == null || area.AreaID == areaId)) {

        const color = getRandomColor();
        const bounds = getAreaBounds(area);

        const rect = L.rectangle(bounds, { color: color, weight: 1 })

        addToggleLayer("SubZones", area.AreaName, rect, false);
    }
}

function addSubZonesTexts(areaId) {
    const container = pixiOverlay.utils.getContainer();
    const groupName = "SubZoneTexts";
    const renderer = pixiOverlay.utils.getRenderer();

    // Step 1: Count duplicates
    const nameCounts = {};
    for (const area of Object.values(SubZones)) {
        nameCounts[area.AreaName] = (nameCounts[area.AreaName] ?? 0) + 1;
    }

    const textStyle = new PIXI.TextStyle({
        fontFamily: 'Arial',
        fontSize: 15,
        fill: '#ffffff',
        stroke: '#000000',
        strokeThickness: 4,
        align: 'center',
        wordWrap: true,
        wordWrapWidth: 150
    });

    for (const area of Object.values(SubZones)
        .filter(area => areaId == null || area.AreaID == areaId)) {

        const label = area.AreaName;

        // Unique toggle label if duplicates
        const toggleLabel = nameCounts[area.AreaName] > 1
            ? `${area.AreaName} - ${area.AreaID}`
            : area.AreaName;

        // Step 2: Create PIXI.Text
        const text = new PIXI.Text(label, textStyle);
        text.anchor.set(0.5);

        // Step 3: Convert to sprite via texture
        const texture = renderer.generateTexture(text);
        const sprite = new PIXI.Sprite(texture);
        sprite.anchor.set(0.5);
        sprite.scale.set(2);

        // Step 4: Positioning
        const bounds = getAreaBounds(area).flat();
        const boundsObj = L.latLngBounds(bounds);
        const northCenterLatLng = L.latLng(boundsObj.getNorth(), boundsObj.getCenter().lng);
        const offsetLat = northCenterLatLng.lat - 2;
        sprite.latlng = L.latLng(offsetLat, northCenterLatLng.lng);

        sprite.visible = true;
        sprite.label = toggleLabel;

        container.addChild(sprite);

        const groupLayer = createPixiSpriteGroupLayer(groupName, [sprite]);
        addToggleLayer(groupName, toggleLabel, groupLayer, true);
    }

    scheduleGroupedLayerControlUpdate();
    schedulePixiRedraw();
}

function flipXYLower(p) {
    return {
        x: p.Y || p.y,
        y: p.X || p.x
    };
}

function revertFlipXYUpper(p) {
    return {
        X: p.X,
        Y: p.Y
    };
}

async function loadMapPath(areaId, path, color = 'blue') {
    const area = Zones[areaId];
    if (area == null) {
        console.error("Area not found: " + areaId);
        return;
    }

    if (config.MapID != area.MapID) {
        console.error("Currently loaded MapID does not match with: " + area.MapID);
        return;
    }

    const response = await fetch(path)
        .catch(e => console.error(e));

    const mapPoints = await response.json();

    var latlngs = [];

    for (const point of mapPoints) {
        const flippedPoint = flipXYLower(point);
        const worldPos = localToWorld(area, flippedPoint);
        latlngs.push(worldTolatLng(worldPos.x, worldPos.y));
    }
    const polyline = L.polyline(latlngs, { color: color });

    polyline.bindPopup(path);

    //.addTo(editableLayers);
    //editablePathLayerControl.addOverlay(polyline, path, true);

    bindRightClick(polyline, 'Paths', path);

    addToggleLayer('Paths', path, polyline, true);
}

async function loadMapPathByFilter(filter, color = 'random') {

    const response = await fetch(`/api/Path?filter=${encodeURIComponent(filter)}`)
        .catch(e => console.error(e));

    const fileNames = await response.json();

    for (const name of fileNames) {
        if (name.includes("optimal") || name.includes("Herb") || name.includes("Vein")) {
            continue;
        }

        // split by "_"" or "\" or "." or " "
        const regex = /[\\_. ]/g;
        const tokens = name.split(regex).filter(Boolean);

        let area = null;

        // Try to find matching zone by AreaName
        for (const rawToken of tokens) {
            const token = rawToken.toLowerCase();
            area = Object.values(Zones).find(zone =>
                zone.AreaName.toLowerCase().includes(token)
            );

            if (area) break;
        }

        // If no match found, try to find by race starting zone
        if (area == null) {
            for (const rawToken of tokens) {
                const token = rawToken.toLowerCase();
                // Try raceToZone mapping
                const raceZoneId = raceToZone[rawToken];
                if (raceZoneId !== undefined) {
                    area = Zones[raceZoneId];
                    if (area) break;
                }
            }
        }

        if (area == null) {
            console.warn("Area not found: " + name + " -- " + filter);
            continue;
        }

        if (config.MapID != area.MapID) {
            console.warn("Currently loaded MapID does not match with: " + area.MapID);
            continue;
        }

        loadMapPath(area.AreaID, name, color == "random" ? getRandomColor() : color);
    }
}


function getRandomColor() {
    return '#' + Math.floor(Math.random() * 0xFFFFFF)
        .toString(16)
        .padStart(6, '0')
        .toUpperCase();
}

///////////////////////////////////////////////////////////////////////
//////////////////// drawing //////////////////////////////////////////
///////////////////////////////////////////////////////////////////////

const drawControl = new L.Control.Draw({
    position: 'topleft',
    draw: {
        polyline: {
            showLength: false,
            shapeOptions: {
                lazyMode: true,
            }
        },
        polygon: {
            allowIntersection: false,
            showArea: true,
            shapeOptions: {
                dashArray: '20,15',
                lineJoin: 'round',
            },
            drawError: {
                timeout: 1000
            },
            shapeOptions: {

            }
        },
        circle: {
            shapeOptions: {
            }
        },
        rectangle: {
            shapeOptions: {
                clickable: true
            }
        },
        marker: false
    },
    edit: {
        featureGroup: editableLayers,
        edit: true,
        remove: true
    }
});

function bindRightClick(layer, groupName, pathName) {
    layer.groupName = groupName;
    layer.PathName = pathName;

    layer.on('contextmenu', function (e) {

        // Middle click saves the path if editable
        layer.on('mousedown', async function (e) {
            if (e.originalEvent.button === 1 && editableLayers.hasLayer(layer)) {
                //e.preventDefault();
                //e.stopPropagation();

                const latlngs = layer.getLatLngs?.();
                if (!latlngs || latlngs.length === 0) {
                    console.warn("Layer has no coordinates to save.");
                    return;
                }

                try {
                    // Step 1: Convert to world coordinates
                    const worldPoints = latlngs.map(latLng => latLngToWorld(latLng));

                    // Step 2: Fetch areaId and Z for each world point
                    const areaDataList = await Promise.all(
                        worldPoints.map(pos => getAreaIdAndZFromService(pos))
                    );

                    // Step 3: Convert each point to map percentage coords
                    const mapPoints = areaDataList.map((areaData, index) => {
                        const world = worldPoints[index];
                        const areaId = areaData.areaId;

                        const map = worldToPercentage(world, areaId); // returns { p: {x, y, z}, ... }
                        return {
                            x: Number(map.p.x),
                            y: Number(map.p.y),
                            z: Number(0)//map.p.z
                        };
                    });

                    // Step 4: Save path
                    await savePath(pathName, mapPoints);
                    console.log(`${pathName} Path saved successfully!`);
                } catch (err) {
                    console.error("Failed to save path:", err);
                }

                // Disable editing and remove from editable group
                if (layer.editing?.disable) {
                    layer.editing.disable();
                }
                editableLayers.removeLayer(layer);
                assignLayer(layer.groupName, layer.PathName, layer, true);
            }
        });

        const isEditable = editableLayers.hasLayer(layer);
        if (isEditable) {
            // Disable editing and remove from editable group
            if (layer.editing?.disable) {
                layer.editing.disable();
            }
            editableLayers.removeLayer(layer);
            assignLayer(layer.groupName, layer.PathName, layer, true);
        } else {
            // Enable editing and add to editable group
            editableLayers.addLayer(layer);
            if (layer.editing?.enable) {
                layer.editing.enable();
            }
        }
    });
}

function flashLayer(layer, duration = 600) {
    const originalStyle = {
        color: layer.options.color,
        weight: layer.options.weight,
        opacity: layer.options.opacity,
        dashArray: layer.options.dashArray
    };

    layer.setStyle({
        color: '#ffff00',
        weight: 6,
        opacity: 1,
        dashArray: '5, 5'
    });

    setTimeout(() => {
        layer.setStyle(originalStyle);
    }, duration);
}

