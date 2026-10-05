// The Control Center's world view (Components/Shared/WorldView.razor): one map for every page that
// shows the world, flat and north-up, tilting into 3D, drawn with deck.gl.
//
// The page owns what is shown - routes, markers, labels - and sends it whole as a scene. The ground
// the view fetches itself from the Pathing Service (forwarded here, same origin): the continent's
// relief, one height per baked tile, when zoomed out; each visible tile's walkable polygons when
// zoomed in. So it needs no minimap art and works for every client.
//
// Coordinates: the page speaks WoW world yards (x north, y west, z up). The view draws X = -y (east
// to the right), Y = x (north up), Z = z.
//
// deck.gl (~2 MB) is fetched the first time a page shows the view; everything lives on
// window.worldView.

(function () {
    'use strict';

    const DECK_URL = 'https://cdn.jsdelivr.net/npm/deck.gl@9.4.0/dist.min.js';
    const DETAIL_SPAN = 1600;        // yards across the view below which tiles show their polygons
    const MAX_DETAIL_TILES = 220;    // more than this in view stays on the relief
    const FETCHES = 6;               // tile requests at once
    const LIFT = 1.5;                // routes and markers float this far above the ground
    const TOP_DOWN = 90;             // rotationX looking straight down
    const TILTED = 50;
    const BACKGROUND = [10 / 255, 14 / 255, 19 / 255, 1];   // --pa-bg

    // What each kind of thing a page sends looks like.
    const KINDS = {
        found: [63, 185, 80],
        partial: [240, 160, 50],
        gap: [240, 160, 50],
        leg: [88, 166, 255],
        recorded: [210, 168, 255],
        start: [63, 185, 80],
        end: [88, 166, 255],
        stop: [248, 81, 73],
        point: [230, 237, 243],
    };
    const WATER = [44, 96, 150];
    const OUTLINE = [8, 12, 16, 110];

    // Walkable height to colour: low sea floor to snow, muted for the dark theme.
    const RAMP = [
        [-80, [22, 44, 70]], [-10, [36, 70, 96]], [0, [58, 92, 74]], [60, [74, 112, 70]],
        [160, [104, 122, 74]], [300, [128, 112, 80]], [500, [138, 120, 104]], [900, [180, 178, 176]],
        [1600, [226, 228, 232]],
    ];

    const instances = new Map();
    let nextId = 1;

    // Looking straight down the map is orthographic, so ground at different heights lines up;
    // tilted, it is in perspective, so heights read as depth.
    function viewOf(tilted) {
        return new deck.OrbitView({ id: 'world', orbitAxis: 'Z', fovy: 40, orthographic: !tilted });
    }

    function toView(x, y, z) { return [-y, x, z || 0]; }

    function fromView(c) { return { x: c[1], y: -c[0], z: c.length > 2 ? c[2] : 0 }; }

    function heightColor(z) {
        if (z <= RAMP[0][0]) return RAMP[0][1];
        for (let i = 1; i < RAMP.length; i++) {
            if (z <= RAMP[i][0]) {
                const [z0, c0] = RAMP[i - 1];
                const [z1, c1] = RAMP[i];
                const t = (z - z0) / (z1 - z0);
                return [0, 1, 2].map(k => Math.round(c0[k] + ((c1[k] - c0[k]) * t)));
            }
        }
        return RAMP[RAMP.length - 1][1];
    }

    function fill(url, values) {
        return url.replace(/\{(\w+)\}/g, (_, key) => encodeURIComponent(values[key]));
    }

    // --- the view ------------------------------------------------------------------------------

    /** Creates a view in host; options.urls name the service's overview and tile routes. */
    async function create(host, dotnet, options) {
        await window.pathingUi.loadScript(DECK_URL);
        const id = 'world-' + (nextId++);
        const view = {
            id, host, dotnet,
            urls: options.urls,
            continent: null,
            overview: null,         // { origin, size, cells: [{ key, x, z, height, polygon }], byKey }
            tiles: new Map(),       // key -> polygons, or null when the tile could not be read
            queue: [], inFlight: 0,
            scene: { routes: [], markers: [], labels: [] },
            viewState: { target: [0, 0, 0], zoom: -4, rotationX: TOP_DOWN, rotationOrbit: 0, minZoom: -7, maxZoom: 5, minRotationX: 5, maxRotationX: TOP_DOWN },
            picking: false,
            readout: host.querySelector('.pa-world-readout'),
            disposed: false,
        };

        view.deck = new deck.Deck({
            parent: host.querySelector('.pa-world-canvas'),
            views: viewOf(false),
            initialViewState: view.viewState,
            controller: { dragMode: 'pan', inertia: 250, scrollZoom: { speed: 0.01, smooth: false }, doubleClickZoom: false },
            parameters: { clearColor: BACKGROUND },
            onViewStateChange: ({ viewState }) => {
                view.viewState = viewState;
                requestDetail(view);
                return viewState;
            },
            onHover: info => showReadout(view, info),
            onClick: info => picked(view, info),
            getTooltip: info => tooltip(info),
            getCursor: ({ isDragging, isHovering }) => isDragging ? 'grabbing' : (view.picking ? 'crosshair' : (isHovering ? 'pointer' : 'grab')),
            layers: [],
        });

        instances.set(id, view);
        return id;
    }

    /** Shows a scene: { continent, routes, markers, labels, frame }. */
    async function setScene(id, scene) {
        const view = instances.get(id);
        if (!view) return;
        view.scene = {
            routes: (scene.routes || []).map(r => ({ ...r, path: pathOf(r.points) })),
            markers: (scene.markers || []).map(m => ({ ...m, position: toView(m.x, m.y, (m.z || 0) + LIFT) })),
            labels: (scene.labels || []).map(l => ({ ...l, position: toView(l.x, l.y, 0) })),
        };

        if (scene.continent && scene.continent !== view.continent) {
            await loadContinent(view, scene.continent);
            if (!scene.frame) frameBounds(view, view.overview ? view.overview.bounds : null);
        }

        if (scene.frame) {
            frameBounds(view, sceneBounds(view) || (view.overview ? view.overview.bounds : null));
        }

        render(view);
    }

    function pathOf(flat) {
        const path = [];
        for (let i = 0; i + 2 < flat.length; i += 3) {
            path.push(toView(flat[i], flat[i + 1], flat[i + 2] + LIFT));
        }
        return path;
    }

    /** Tilts the map to show heights, or looks straight down again. */
    function setTilt(id, tilted) {
        const view = instances.get(id);
        if (!view) return;
        view.deck.setProps({ views: viewOf(tilted) });
        moveTo(view, { rotationX: tilted ? TILTED : TOP_DOWN, rotationOrbit: tilted ? view.viewState.rotationOrbit : 0 });
    }

    /** Frames what the page shows, or the continent when it shows nothing. */
    function fit(id) {
        const view = instances.get(id);
        if (!view) return;
        frameBounds(view, sceneBounds(view) || (view.overview ? view.overview.bounds : null));
    }

    /** While on, a click on the map is sent to the page's OnWorldClick. */
    function setPicking(id, on) {
        const view = instances.get(id);
        if (view) view.picking = !!on;
    }

    function dispose(id) {
        const view = instances.get(id);
        if (!view) return;
        view.disposed = true;
        instances.delete(id);
        view.deck.finalize();
    }

    // --- the ground ----------------------------------------------------------------------------

    async function loadContinent(view, continent) {
        view.continent = continent;
        view.overview = null;
        view.tiles = new Map();
        view.queue = [];
        try {
            const response = await fetch(fill(view.urls.overview, { continent }));
            if (!response.ok) throw new Error(response.status + ' ' + response.statusText);
            const data = await response.json();
            if (view.disposed || view.continent !== continent) return;
            view.overview = overviewOf(data);
        } catch (e) {
            window.pathingUi.log('The relief of ' + continent + ' could not be read: ' + e.message);
        }
    }

    function overviewOf(data) {
        const size = data.tileSize;
        const cells = [];
        const byKey = new Map();
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (let i = 0; i < data.heights.length; i++) {
            const tx = data.tiles[i * 2];
            const tz = data.tiles[(i * 2) + 1];
            const height = data.heights[i];
            const y0 = data.origin + (tx * size);
            const x0 = data.origin + (tz * size);
            const cell = {
                key: tx + ',' + tz, tx, tz, height,
                polygon: [toView(x0, y0, height), toView(x0, y0 + size, height), toView(x0 + size, y0 + size, height), toView(x0 + size, y0, height)],
            };
            cells.push(cell);
            byKey.set(cell.key, cell);
            minX = Math.min(minX, -(y0 + size)); maxX = Math.max(maxX, -y0);
            minY = Math.min(minY, x0); maxY = Math.max(maxY, x0 + size);
        }
        return { origin: data.origin, size, cells, byKey, bounds: cells.length ? [minX, minY, maxX, maxY] : null };
    }

    // The baked tiles around the view's target, when it is close enough to draw their polygons.
    function detailTiles(view) {
        const o = view.overview;
        if (!o) return [];
        const span = viewSpan(view);
        if (span > DETAIL_SPAN) return [];

        const [cx, cy] = view.viewState.target;
        const half = span * 0.75;
        // View X = -wow y, view Y = wow x: tile x runs along wow y, tile z along wow x.
        const txMin = Math.floor((-(cx + half) - o.origin) / o.size);
        const txMax = Math.floor((-(cx - half) - o.origin) / o.size);
        const tzMin = Math.floor(((cy - half) - o.origin) / o.size);
        const tzMax = Math.floor(((cy + half) - o.origin) / o.size);
        const tiles = [];
        for (let tx = txMin; tx <= txMax; tx++) {
            for (let tz = tzMin; tz <= tzMax; tz++) {
                const cell = o.byKey.get(tx + ',' + tz);
                if (cell) tiles.push(cell);
            }
        }
        return tiles.length <= MAX_DETAIL_TILES ? tiles : [];
    }

    function viewSpan(view) {
        const width = view.host.clientWidth || 1000;
        const height = view.host.clientHeight || 800;
        return Math.max(width, height) / Math.pow(2, view.viewState.zoom);
    }

    let detailTimer = 0;
    function requestDetail(view) {
        clearTimeout(detailTimer);
        detailTimer = setTimeout(() => {
            for (const cell of detailTiles(view)) {
                if (!view.tiles.has(cell.key) && !view.queue.includes(cell)) view.queue.push(cell);
            }
            pump(view);
            render(view);
        }, 120);
    }

    function pump(view) {
        while (view.inFlight < FETCHES && view.queue.length > 0) {
            const cell = view.queue.shift();
            if (view.tiles.has(cell.key)) continue;
            view.inFlight++;
            const continent = view.continent;
            fetch(fill(view.urls.tile, { continent, x: cell.tx, z: cell.tz }))
                .then(r => r.ok ? r.json() : null)
                .then(data => {
                    if (view.continent === continent) view.tiles.set(cell.key, data ? polygonsOf(data) : null);
                })
                .catch(() => view.tiles.set(cell.key, null))
                .finally(() => {
                    view.inFlight--;
                    if (!view.disposed) {
                        render(view);
                        pump(view);
                    }
                });
        }
    }

    function polygonsOf(tile) {
        const v = tile.vertices;
        const polygons = [];
        let p = 0;
        for (let i = 0; i < tile.polygons.length; i += tile.polygons[i] + 1, p++) {
            const ring = [];
            let height = 0;
            const count = tile.polygons[i];
            for (let k = 1; k <= count; k++) {
                const j = tile.polygons[i + k] * 3;
                ring.push(toView(v[j], v[j + 1], v[j + 2]));
                height += v[j + 2];
            }
            polygons.push({ polygon: ring, height: height / count, water: tile.water[p] === 1 });
        }
        return polygons;
    }

    // --- drawing -------------------------------------------------------------------------------

    function render(view) {
        if (view.disposed) return;
        const layers = [];
        const detail = detailTiles(view);
        const shown = new Set(detail.filter(c => view.tiles.get(c.key)).map(c => c.key));

        if (view.overview) {
            const cells = shown.size ? view.overview.cells.filter(c => !shown.has(c.key)) : view.overview.cells;
            layers.push(new deck.SolidPolygonLayer({
                id: 'relief', data: cells, _full3d: true, pickable: true, material: false,
                getPolygon: c => c.polygon,
                getFillColor: c => heightColor(c.height),
                updateTriggers: { getPolygon: shown.size },
            }));
        }

        for (const cell of detail) {
            const polygons = view.tiles.get(cell.key);
            if (!polygons) continue;
            layers.push(new deck.PolygonLayer({
                id: 'tile-' + cell.key, data: polygons, _full3d: true, pickable: true, material: false,
                stroked: true, filled: true, lineWidthUnits: 'pixels', getLineWidth: 1,
                getPolygon: p => p.polygon,
                getFillColor: p => p.water ? WATER : heightColor(p.height),
                getLineColor: OUTLINE,
            }));
        }

        const over = { depthCompare: 'always' };
        const labels = viewSpan(view) > DETAIL_SPAN ? view.scene.labels : [];
        layers.push(new deck.TextLayer({
            id: 'labels', data: labels, getPosition: l => l.position, getText: l => l.text,
            getSize: 12, sizeUnits: 'pixels', getColor: [200, 210, 222, 190], fontWeight: 600,
            fontFamily: 'ui-sans-serif, system-ui, Segoe UI, sans-serif', parameters: over,
        }));

        const routes = view.scene.routes.filter(r => r.kind !== 'gap');
        const gaps = view.scene.routes.filter(r => r.kind === 'gap');
        layers.push(new deck.PathLayer({
            id: 'routes', data: routes, getPath: r => r.path, getColor: r => KINDS[r.kind] || KINDS.point,
            getWidth: 4, widthUnits: 'pixels', capRounded: true, jointRounded: true, pickable: true, parameters: over,
        }));
        layers.push(new deck.PathLayer({
            id: 'gaps', data: gaps, getPath: r => r.path, getColor: r => KINDS[r.kind] || KINDS.point,
            getWidth: 2, widthUnits: 'pixels', getDashArray: [6, 5], dashJustified: true,
            extensions: deck.PathStyleExtension ? [new deck.PathStyleExtension({ dash: true })] : [],
            parameters: over,
        }));
        layers.push(new deck.ScatterplotLayer({
            id: 'markers', data: view.scene.markers, getPosition: m => m.position,
            getFillColor: m => KINDS[m.kind] || KINDS.point, getLineColor: [255, 255, 255, 230], stroked: true,
            radiusUnits: 'pixels', getRadius: 7, lineWidthUnits: 'pixels', getLineWidth: 2, pickable: true, parameters: over,
        }));
        layers.push(new deck.TextLayer({
            id: 'marker-labels', data: view.scene.markers.filter(m => m.label), getPosition: m => m.position,
            getText: m => m.label, getSize: 13, sizeUnits: 'pixels', getColor: [230, 237, 243],
            getPixelOffset: [0, -18], background: true, getBackgroundColor: [16, 22, 30, 225], backgroundPadding: [6, 3],
            fontFamily: 'ui-sans-serif, system-ui, Segoe UI, sans-serif', parameters: over,
        }));

        view.deck.setProps({ layers });
    }

    function sceneBounds(view) {
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        const take = p => {
            minX = Math.min(minX, p[0]); maxX = Math.max(maxX, p[0]);
            minY = Math.min(minY, p[1]); maxY = Math.max(maxY, p[1]);
        };
        view.scene.routes.forEach(r => r.path.forEach(take));
        view.scene.markers.forEach(m => take(m.position));
        return minX <= maxX ? [minX, minY, maxX, maxY] : null;
    }

    function frameBounds(view, bounds) {
        if (!bounds) return;
        const [minX, minY, maxX, maxY] = bounds;
        const width = view.host.clientWidth || 1000;
        const height = view.host.clientHeight || 800;
        const spanX = Math.max(maxX - minX, 150);
        const spanY = Math.max(maxY - minY, 150);
        const zoom = Math.log2(Math.min(width / spanX, height / spanY)) - 0.35;
        moveTo(view, { target: [(minX + maxX) / 2, (minY + maxY) / 2, 0], zoom });
    }

    function moveTo(view, changes) {
        const next = { ...view.viewState, ...changes, transitionDuration: 450 };
        view.viewState = next;
        view.deck.setProps({ initialViewState: next });
        requestDetail(view);
    }

    // --- pointer -------------------------------------------------------------------------------

    function showReadout(view, info) {
        if (!view.readout) return;
        if (!info.coordinate) {
            view.readout.textContent = '';
            return;
        }
        const w = fromView(info.coordinate);
        const height = info.object && info.object.height !== undefined ? info.object.height : null;
        view.readout.textContent = 'x ' + w.x.toFixed(1) + '   y ' + w.y.toFixed(1) +
            (height !== null ? '   height ' + height.toFixed(1) : '');
    }

    function picked(view, info) {
        if (!view.picking || !info.coordinate) return;
        const w = fromView(info.coordinate);
        // Over a polygon its height says which floor; elsewhere 0 lets the service find the ground.
        const height = info.object && info.object.height !== undefined ? info.object.height : 0;
        view.dotnet.invokeMethodAsync('OnWorldClick', w.x, w.y, height).catch(() => { });
    }

    function tooltip(info) {
        const o = info.object;
        if (!o) return null;
        let text = null;
        if (info.layer.id === 'routes') {
            text = (o.label || o.kind) + ' · ' + o.path.length + ' points';
        } else if (info.layer.id === 'markers') {
            text = o.label || o.kind;
        } else if (info.layer.id === 'relief') {
            text = 'tile ' + o.tx + ', ' + o.tz + ' · walkable height ' + o.height.toFixed(0);
        } else if (o.polygon) {
            text = (o.water ? 'water' : 'ground') + ' · height ' + o.height.toFixed(1);
        }
        return text ? { text, style: { background: 'rgba(16, 22, 30, 0.95)', color: '#e6edf3', fontSize: '12px', padding: '4px 8px', borderRadius: '6px' } } : null;
    }

    /** What every view holds, for a browser check: its continent, ground and layers. */
    function describe() {
        return [...instances.values()].map(v => ({
            continent: v.continent,
            reliefTiles: v.overview ? v.overview.cells.length : 0,
            detailTiles: [...v.tiles.values()].filter(t => t).length,
            viewState: { target: v.viewState.target, zoom: v.viewState.zoom, rotationX: v.viewState.rotationX },
            layers: (v.deck.props.layers || []).map(l => l.id + ':' + (Array.isArray(l.props.data) ? l.props.data.length : '?')),
        }));
    }

    window.worldView = { create, setScene, setTilt, fit, setPicking, dispose, describe };
})();
