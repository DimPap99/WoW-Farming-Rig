// The live view over the Pathing Service's watch hub, proxied by the Control Center; nothing happens where signalR is not loaded.
// Part of the Leaflet map, loaded after the pieces before it; see scripts/leaflet-pieces.mjs.

/////////////////////////////////////////////////
////////////// signalR ///////////////////////////
/////////////////////////////////////////////////

window.addEventListener('DOMContentLoaded', function () {

    if (typeof signalR === "undefined" || signalR == null) {
        return;
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/watchHub")
        .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
        .withAutomaticReconnect([0, 3000, 5000, 10000, 15000, 30000])
        .build();

    connection.start().then(function () {
        console.log("Connected to SignalR Hub");
    }).catch(function (err) {
        return console.error(err.toString());
    });

    removeMesh = function (name) {
        const layer = layerNames[name];
        if (!layer) return;

        if (LeafletMap.hasLayer(layer)) {
            requestAnimationFrame(() => {
                LeafletMap.removeLayer(layer);
            });
        }

        if (layerNames[name]) {
            delete layerNames[name];
        }

        scheduleGroupedLayerControlUpdate();
    }

    clear = function () {

        const groupName = 'Watch';

        const layers = groupedOverlays[groupName];
        for (const layerName in layers) {
            const layer = layers[layerName];
            if (LeafletMap.hasLayer(layer)) {
                requestAnimationFrame(() => {
                    LeafletMap.removeLayer(layer);
                });
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

    getColor = function (color) {
        switch (color) {
            case 1: return "#FF0000"; // Red
            case 2: return "#008000"; // Green
            case 3: return "#0000FF"; // Blue
            case 4: return "#008080"; // Teal
            case 5: return "#008080"; // Teal again
            case 6: return "#FF9900"; // Orange
            case 7: return "#FFFF00"; // Yellow
            case 8: return "#000000"; // Black
            case 9: return "#FF00FF"; // Magenta
            case 10: return "#00FFFF"; // Cyan or something else if needed
            default: return "#FFFFFF"; // White
        }
    };

    connection.on("removeMesh", removeMesh);
    connection.on("clear", clear);

    connection.on("drawLine", (array, height, color, name) => {
        if (array.length === 0) return;
        if (LeafletMap == null) return;

        const existing = layerNames[name];
        if (existing && existing.setLatLng) {
            requestAnimationFrame(() => {
                const latlng = worldTolatLng(array[0], array[1]);
                existing.setLatLng(latlng);
            });
            return;
        }
        requestAnimationFrame(async () => {

            addGroupLayer('Watch', 'Watch');

            const markerIcon = L.divIcon({
                className: 'poiatlas',
                iconSize: [aSize, aSize],
                html: atlasImgName('poi')
            });

            const worldPos = { x: array[0], y: array[1] };

            const latlng = worldTolatLng(worldPos.x, worldPos.y);

            const marker = new L.marker(latlng, { icon: markerIcon });

            bindPopupEnrichCoordinates(marker, worldPos, name);

            addToggleLayer('Watch', name, marker);

            scheduleGroupedLayerControlUpdate();
        });
    });

    connection.on("drawPath", (arrays, color, name) => {
        if (arrays.length === 0) return;
        if (LeafletMap == null) return;

        const latlngs = arrays.map(([x, y]) => worldTolatLng(x, y));
        const existing = layerNames[name];
        if (existing && existing.setLatLngs) {
            requestAnimationFrame(() => {
                existing.setLatLngs(latlngs);
                existing.setStyle({ color: getColor(color) });
                existing.bringToFront();
            });
            return;
        }

        requestAnimationFrame(() => {

            addGroupLayer('Watch', 'Watch');

            const resolvedColor = getColor(color);

            // A 1px line disappears against the map art - Westfall's orange in
            // particular swallows a thin red path completely. Draw a dark casing
            // under a thicker coloured line so the route reads on any terrain.
            const casing = L.polyline(latlngs, {
                color: '#000000',
                weight: PATH_WEIGHT + 3,
                opacity: 0.45
            });

            const polyline = L.polyline(latlngs, {
                color: resolvedColor,
                weight: PATH_WEIGHT,
                opacity: 1
            }).bindPopup(name);

            const group = L.layerGroup([casing, polyline]);
            group.setLatLngs = (ll) => {
                casing.setLatLngs(ll);
                polyline.setLatLngs(ll);
            };
            group.setStyle = (s) => polyline.setStyle(s);
            group.bringToFront = () => {
                casing.bringToFront();
                polyline.bringToFront();
            };

            addToggleLayer('Watch', name, group);
            group.bringToFront();
            scheduleGroupedLayerControlUpdate();
        });
    });
});