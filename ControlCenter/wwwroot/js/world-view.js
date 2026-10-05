// The Control Center's world view (Components/Shared/WorldView.razor): one map for every page that
// shows the world, flat and north-up, tilting into 3D, drawn with deck.gl.
//
// The page owns what is shown - routes, markers, labels, shapes - and sends it whole as a scene. The
// ground the view fetches itself from the Pathing Service (forwarded here, same origin): the
// continent's relief, one height per baked tile, when zoomed out; each visible tile's walkable
// polygons when zoomed in. So it needs no minimap art and works for every client. Drawing and
// editing shapes live in world-edit.js, which this view hands its pointer and keys to.
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
    const LABEL_SPAN = 3200;         // yards across the view below which every shape is named
    const MAX_DETAIL_TILES = 220;    // more than this in view stays on the relief
    const FETCHES = 6;               // tile requests at once
    const LIFT = 1.5;                // routes, markers and shapes float this far above the ground
    const TOP_DOWN = 90;             // rotationX looking straight down
    const TILTED = 50;
    const CENTER_SPAN = 1200;        // yards across the view after centring on a point from far out
    const CLICK_SLOP_PX = 4;         // a right-button press that moved further turned the map: not a click
    const BACKGROUND = [10 / 255, 14 / 255, 19 / 255, 1];   // --pa-bg
    const FONT = 'ui-sans-serif, system-ui, Segoe UI, sans-serif';

    // What each kind of route or marker a page sends looks like.
    const KINDS = {
        found: [63, 185, 80],
        partial: [240, 160, 50],
        gap: [240, 160, 50],
        failed: [248, 81, 73],
        pending: [139, 148, 158],
        recent: [120, 146, 182, 170],
        npc: [57, 197, 207],
        leg: [88, 166, 255],
        recorded: [210, 168, 255],
        start: [63, 185, 80],
        end: [88, 166, 255],
        stop: [248, 81, 73],
        point: [230, 237, 243],
    };
    // Kinds drawn dashed: where there is no route, only the straight line it should take.
    const DASHED = new Set(['gap', 'failed', 'pending']);
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
            teardown: [],
            rightDown: null,
            disposed: false,
        };
        const canvas = host.querySelector('.pa-world-canvas');

        view.deck = new deck.Deck({
            parent: canvas,
            views: viewOf(false),
            initialViewState: view.viewState,
            controller: { dragMode: 'pan', inertia: 250, scrollZoom: { speed: 0.01, smooth: false }, doubleClickZoom: false },
            parameters: { clearColor: BACKGROUND },
            onViewStateChange: ({ viewState }) => {
                view.viewState = viewState;
                requestDetail(view);
                return viewState;
            },
            onHover: info => {
                window.worldEdit.hover(view, info);
                showReadout(view, info);
            },
            onClick: info => {
                if (window.worldEdit.click(view, info)) return;
                if (info.layer && info.layer.id === 'markers' && info.object && !view.picking) {
                    view.dotnet.invokeMethodAsync('OnMarkerClicked', info.object.id).catch(() => { });
                    return;
                }
                picked(view, info);
            },
            // Deck hears a drag before the map's controller does: a drag on a handle stops it there, so
            // the handle moves and the map stays.
            onDragStart: (info, event) => {
                if (event.leftButton && window.worldEdit.dragStart(view, info)) event.stopImmediatePropagation();
            },
            onDrag: (info, event) => {
                if (window.worldEdit.drag(view, info)) event.stopImmediatePropagation();
            },
            onDragEnd: (info, event) => {
                if (window.worldEdit.dragEnd(view, info)) event.stopImmediatePropagation();
            },
            // Quiet while drawing: the pointer is placing corners, not asking what is there.
            getTooltip: info => view.edit.tool ? null : tooltip(info),
            getCursor: ({ isDragging, isHovering }) => window.worldEdit.cursor(view) ||
                (isDragging ? 'grabbing' : (view.picking ? 'crosshair' : (isHovering ? 'pointer' : 'grab'))),
            layers: [],
        });

        window.worldEdit.attach(view, {
            toView, fromView, lift: LIFT,
            groundAt: (x, y, info) => groundAt(view, x, y, info),
            closeIn: () => viewSpan(view) < LABEL_SPAN,
            emit: (method, ...args) => view.dotnet.invokeMethodAsync(method, ...args).catch(() => { }),
            render: () => render(view),
        });

        // A right-click removes a vertex. A right-drag turns the map, and the browser sends its
        // context menu event after that too, so only a press that stayed put counts as a click.
        // deck.gl makes the canvas's element focusable for its own keys (arrows pan); a press on
        // the map gives it the focus, so the tools' keys (Escape, Enter, Backspace, Delete) work.
        listen(view, canvas, 'pointerdown', e => {
            view.rightDown = e.button === 2 ? { x: e.clientX, y: e.clientY } : null;
            canvas.focus({ preventScroll: true });
        });
        listen(view, canvas, 'contextmenu', e => {
            e.preventDefault();
            const down = view.rightDown;
            view.rightDown = null;
            if (down && Math.hypot(e.clientX - down.x, e.clientY - down.y) > CLICK_SLOP_PX) return;
            const box = canvas.getBoundingClientRect();
            window.worldEdit.contextClick(view, e.clientX - box.left, e.clientY - box.top);
        });
        listen(view, canvas, 'keydown', e => {
            if (window.worldEdit.key(view, e)) {
                e.preventDefault();
                e.stopPropagation();
            }
        });
        // A tool's button switches the tool here at once, so a click on the map right after it is the
        // new tool's; the page's word on the tool follows over the circuit.
        listen(view, host, 'click', e => {
            const button = e.target.closest ? e.target.closest('.pa-world-tool') : null;
            if (!button) return;
            window.worldEdit.setTool(view, button.getAttribute('aria-pressed') === 'true' ? null : button.dataset.tool);
        });

        instances.set(id, view);
        return id;
    }

    function listen(view, target, type, handler) {
        target.addEventListener(type, handler);
        view.teardown.push(() => target.removeEventListener(type, handler));
    }

    /** Shows a scene: { continent, routes, markers, labels, shapes, selected, frame }. */
    async function setScene(id, scene) {
        const view = instances.get(id);
        if (!view) return;
        view.scene = {
            routes: (scene.routes || []).map(r => ({ ...r, path: pathOf(r.points) })),
            markers: (scene.markers || []).map(m => ({ ...m, position: toView(m.x, m.y, (m.z || 0) + LIFT) })),
            labels: (scene.labels || []).map(l => ({ ...l, position: toView(l.x, l.y, 0) })),
        };
        window.worldEdit.setShapes(view, scene.shapes, scene.selected);

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

    /** Names what a click draws: point, line, polygon, points, rectangle or circle; null for none. */
    function setTool(id, tool) {
        const view = instances.get(id);
        if (view) window.worldEdit.setTool(view, tool);
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

    /** Centres the view on a world point, closing in when it is zoomed far out. */
    function centerOn(id, x, y) {
        const view = instances.get(id);
        if (!view) return;
        const zoom = viewSpan(view) > CENTER_SPAN * 2 ? zoomFor(view, CENTER_SPAN, CENTER_SPAN) : view.viewState.zoom;
        const [vx, vy] = toView(x, y, 0);
        moveTo(view, { target: [vx - beside(view, zoom), vy, 0], zoom });
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
        view.teardown.forEach(undo => undo());
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

    function isGround(layerId) {
        return layerId === 'relief' || layerId.startsWith('tile-');
    }

    /**
     * The ground under a screen point, in view coordinates lifted as the scene is. On a tile's
     * polygons it has their height; on the relief (one height per tile) or off the navmesh it is
     * marked unknown, which the page hears as height 0: "find the ground here".
     */
    function groundAt(view, x, y, info) {
        let hit = info && info.layer && isGround(info.layer.id) && info.coordinate ? info : null;
        if (!hit) {
            const ids = (view.deck.props.layers || []).map(l => l.id).filter(isGround);
            hit = ids.length ? view.deck.pickObject({ x, y, radius: 0, layerIds: ids, unproject3D: true }) : null;
        }
        if (hit && hit.coordinate) {
            const p = [hit.coordinate[0], hit.coordinate[1], (hit.coordinate[2] || 0) + LIFT];
            if (hit.layer.id === 'relief') p.unknown = true;
            return p;
        }

        const viewport = view.deck.getViewports()[0];
        if (!viewport) return null;
        const c = viewport.unproject([x, y]);
        const p = [c[0], c[1], LIFT];
        p.unknown = true;
        return p;
    }

    // --- drawing -------------------------------------------------------------------------------

    function render(view) {
        if (view.disposed) return;
        const layers = [];
        const detail = detailTiles(view);
        const shown = new Set(detail.filter(c => view.tiles.get(c.key)).map(c => c.key));

        // The ground picks in 3D, so a click or a hover on it knows its height even when tilted.
        if (view.overview) {
            const cells = shown.size ? view.overview.cells.filter(c => !shown.has(c.key)) : view.overview.cells;
            layers.push(new deck.SolidPolygonLayer({
                id: 'relief', data: cells, _full3d: true, pickable: '3d', material: false,
                getPolygon: c => c.polygon,
                getFillColor: c => heightColor(c.height),
                updateTriggers: { getPolygon: shown.size },
            }));
        }

        for (const cell of detail) {
            const polygons = view.tiles.get(cell.key);
            if (!polygons) continue;
            layers.push(new deck.PolygonLayer({
                id: 'tile-' + cell.key, data: polygons, _full3d: true, pickable: '3d', material: false,
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
            fontFamily: FONT, parameters: over,
        }));

        // Shapes under the routes, so a route drawn through an area stays readable.
        layers.push(...window.worldEdit.shapeLayers(view));

        const solid = view.scene.routes.filter(r => !DASHED.has(r.kind));
        const dashed = view.scene.routes.filter(r => DASHED.has(r.kind));
        // Faint routes first, so the one that matters is drawn over them.
        solid.sort((a, b) => (b.kind === 'recent') - (a.kind === 'recent'));
        layers.push(new deck.PathLayer({
            id: 'routes', data: solid, getPath: r => r.path, getColor: r => KINDS[r.kind] || KINDS.point,
            getWidth: r => r.kind === 'recent' ? 2 : 4, widthUnits: 'pixels', capRounded: true, jointRounded: true,
            pickable: true, parameters: over,
        }));
        layers.push(new deck.PathLayer({
            id: 'gaps', data: dashed, getPath: r => r.path, getColor: r => KINDS[r.kind] || KINDS.point,
            getWidth: 2, widthUnits: 'pixels', getDashArray: [6, 5], dashJustified: true, pickable: true,
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
            fontFamily: FONT, parameters: over,
        }));

        // Handles and the draft over everything: what the pointer is working on.
        layers.push(...window.worldEdit.overlayLayers(view));

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
        view.edit.shapes.forEach(s => {
            s.vertices.forEach(take);
            if (s.type === 'circle' && s.vertices.length > 0) {
                const [x, y, z] = s.vertices[0];
                take([x - s.radius, y - s.radius, z]);
                take([x + s.radius, y + s.radius, z]);
            }
        });
        return minX <= maxX ? [minX, minY, maxX, maxY] : null;
    }

    // The width of the view a tall panel at its side does not cover, as [left, right] pixels: what
    // the page shows is framed and centred there, not behind the panel.
    function uncovered(view) {
        const host = view.host.getBoundingClientRect();
        let left = 0;
        let right = host.width;
        view.host.querySelectorAll('.pa-panel').forEach(panel => {
            const box = panel.getBoundingClientRect();
            if (box.width === 0 || box.height < host.height * 0.4) return;
            if (box.left - host.left < host.width / 2) left = Math.max(left, box.right - host.left);
            else right = Math.min(right, box.left - host.left);
        });
        return right - left > 200 ? [left, right] : [0, host.width];
    }

    // How far right of the canvas's middle the uncovered width's middle is, in yards at this zoom: the
    // target moves left by that much for a point to show there.
    function beside(view, zoom) {
        const [left, right] = uncovered(view);
        const width = view.host.clientWidth || 1000;
        return ((left + right - width) / 2) / Math.pow(2, zoom);
    }

    function zoomFor(view, spanX, spanY) {
        const [left, right] = uncovered(view);
        const height = view.host.clientHeight || 800;
        return Math.log2(Math.min((right - left) / spanX, height / spanY)) - 0.35;
    }

    function frameBounds(view, bounds) {
        if (!bounds) return;
        const [minX, minY, maxX, maxY] = bounds;
        const zoom = zoomFor(view, Math.max(maxX - minX, 150), Math.max(maxY - minY, 150));
        moveTo(view, { target: [((minX + maxX) / 2) - beside(view, zoom), (minY + maxY) / 2, 0], zoom });
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
        const layer = info.layer ? info.layer.id : '';
        // A tile's polygons have the ground's own height; the relief only the tile's.
        const height = layer.startsWith('tile-') ? 'height ' + w.z.toFixed(1)
            : (layer === 'relief' ? 'tile height ' + w.z.toFixed(0) : null);
        view.readout.textContent = 'x ' + w.x.toFixed(1) + '   y ' + w.y.toFixed(1) + (height ? '   ' + height : '');
    }

    function picked(view, info) {
        if (!view.picking || info.x === undefined) return;
        const p = groundAt(view, info.x, info.y, info);
        if (!p) return;
        const w = fromView(p);
        view.dotnet.invokeMethodAsync('OnWorldClick', w.x, w.y, p.unknown ? 0 : w.z - LIFT).catch(() => { });
    }

    function tooltip(info) {
        const o = info.object;
        if (!o) return null;
        let text = null;
        if (info.layer.id === 'routes' || info.layer.id === 'gaps') {
            text = o.label || o.kind;
        } else if (info.layer.id === 'markers') {
            text = o.title || o.label || o.kind;
        } else if (info.layer.id === 'shape-areas' || info.layer.id === 'shape-circles' || info.layer.id === 'shape-lines') {
            text = o.label || null;
        } else if (info.layer.id === 'relief') {
            text = 'tile ' + o.tx + ', ' + o.tz + ' · walkable height ' + o.height.toFixed(0);
        } else if (o.polygon) {
            text = (o.water ? 'water' : 'ground') + ' · height ' + o.height.toFixed(1);
        }
        return text ? { text, style: { background: 'rgba(16, 22, 30, 0.95)', color: '#e6edf3', fontSize: '12px', padding: '4px 8px', borderRadius: '6px' } } : null;
    }

    /** What every view holds, for a browser check: its continent, ground, layers, markers on the screen and tools. */
    function describe() {
        return [...instances.values()].map(v => {
            const viewport = v.deck.getViewports()[0];
            return {
                id: v.id,
                continent: v.continent,
                reliefTiles: v.overview ? v.overview.cells.length : 0,
                detailTiles: [...v.tiles.values()].filter(t => t).length,
                viewState: { target: v.viewState.target, zoom: v.viewState.zoom, rotationX: v.viewState.rotationX },
                layers: (v.deck.props.layers || []).map(l => l.id + ':' + (Array.isArray(l.props.data) ? l.props.data.length : '?')),
                routes: v.scene.routes.map(r => r.id + ':' + r.kind + ':' + r.path.length),
                markers: viewport ? v.scene.markers.map(m => {
                    const [x, y] = viewport.project(m.position);
                    return { id: m.id, kind: m.kind, x, y };
                }) : [],
                edit: window.worldEdit.describe(v),
            };
        });
    }

    /** Where a world point is on the screen, relative to the view's canvas, for a browser check. */
    function screenOf(id, x, y, z) {
        const view = instances.get(id);
        const viewport = view ? view.deck.getViewports()[0] : null;
        if (!viewport) return null;
        const [sx, sy] = viewport.project(toView(x, y, (z || 0) + LIFT));
        return { x: sx, y: sy };
    }

    window.worldView = { create, setScene, setTool, setTilt, fit, centerOn, setPicking, dispose, describe, screenOf };
})();
