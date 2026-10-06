// The Control Center's world view (Components/Shared/WorldView.razor): one map for every page that
// shows the world, flat and north-up, tilting into 3D, drawn with deck.gl.
//
// The page owns what is shown - routes, markers, labels, shapes - and sends it whole as a scene. The
// ground the view fetches itself from the Pathing Service (forwarded here, same origin): the
// continent's relief, one height per baked tile, when zoomed out; each visible tile's walkable
// polygons when zoomed in. So it needs no minimap art and works for every client. The zone maps
// and the area grid name the pointer's place in the readout. Drawing and editing shapes live in
// world-edit.js, which this view hands its pointer and keys to.
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
    const GROUND_CLEAR = [10, 14, 19];                      // --pa-bg: where nothing is baked or walkable
    const BACKGROUND = [...GROUND_CLEAR.map(c => c / 255), 1];
    const LABEL_PX = 12;             // zone names' size
    const LABEL_GAP = 3;             // the room a zone name keeps either side: its outline and a space
    const AREA_CELL = 16;            // yards: points this close share a zone (ZoneLookup.CellYd)
    // The map art: the game's minimap in 256-pixel tiles, an ADT (533.33 yards) in 512 pixels at the
    // finest zoom, placed from the middle of the world's 64 x 64 ADTs (MapArt in the contracts).
    const ADT = 1600 / 3;
    const HALF_WORLD = 32 * ADT;
    const ART_TILE = 256;
    const ART_FETCHES = 8;           // art tiles fetched at once
    const ART_KEPT = 320;            // art tiles kept in memory, the least lately drawn dropped first
    const ART_MESH_ALPHA = 70;       // a tile's walkable polygons over the art: see-through
    const AREA_REST_MS = 120;        // the pointer rests this long before its zone is asked for
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
        elite: [229, 83, 75],
        herb: [87, 171, 90],
        vein: [198, 144, 38],
        skinnable: [212, 180, 90],
        mailbox: [108, 182, 255],
        place: [230, 237, 243],
        mark: [217, 112, 214],
        leg: [88, 166, 255],
        recorded: [210, 168, 255],
        start: [63, 185, 80],
        end: [88, 166, 255],
        stop: [248, 81, 73],
        point: [230, 237, 243],
    };
    // Kinds drawn dashed: where there is no route, only the straight line it should take.
    const DASHED = new Set(['gap', 'failed', 'pending']);
    // Markers that come by the hundred - spawns, nodes - drawn smaller.
    const SMALL = new Set(['npc', 'elite', 'herb', 'vein', 'skinnable', 'mailbox', 'place']);
    const WATER = [44, 96, 150];
    const OUTLINE = [8, 12, 16, 110];
    const CASING = [8, 12, 16, 210];         // the dark under a route
    const CASING_FAINT = [8, 12, 16, 110];
    const CASING_PX = 3;                     // how much wider than its route
    const GAP_DASH = [6, 5];                 // a dashed route's dash and gap, in its widths

    // Walkable height to colour: low sea floor to snow, muted for the dark theme.
    const RAMP = [
        [-80, [22, 44, 70]], [-10, [36, 70, 96]], [0, [58, 92, 74]], [60, [74, 112, 70]],
        [160, [104, 122, 74]], [300, [128, 112, 80]], [500, [138, 120, 104]], [900, [180, 178, 176]],
        [1600, [226, 228, 232]],
    ];
    // The relief is lit from the north-west, 45 degrees up, as maps are, with its slopes made three
    // times as steep as they are: a tile is 133 yards across, so real slopes barely shade.
    const SUN = [-0.5, 0.5, Math.SQRT1_2];
    const EXAGGERATION = 3;
    const RELIEF_PIXELS = 4;         // the flat relief's pixels to a tile's side
    const UNBAKED = 0.35;            // how bright the relief stays where no tile is baked
    const LIT_MAX = 232;             // sunlit snow stays under white, so white markers show on it

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

    // heightColor by the whole yard, flat (r, g, b, ...) from the ramp's bottom to its top: the relief
    // picture asks it for every pixel.
    let rampTable = null;
    function rampOffset(z) {
        const bottom = RAMP[0][0];
        const top = RAMP[RAMP.length - 1][0];
        if (!rampTable) {
            rampTable = new Uint8Array((top - bottom + 1) * 3);
            for (let h = bottom; h <= top; h++) rampTable.set(heightColor(h), (h - bottom) * 3);
        }
        return (Math.max(bottom, Math.min(top, Math.round(z))) - bottom) * 3;
    }

    function fill(url, values) {
        return url.replace(/\{(\w+)\}/g, (_, key) => encodeURIComponent(values[key]));
    }

    // --- the view ------------------------------------------------------------------------------

    /** Creates a view in host; options.urls name the service's routes it reads (overview, tile, world, area, art), options.art whether the map art shows. */
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
            tilted: false,
            picking: false,
            artOn: options.art !== false,
            artTiles: new Map(),    // 'z/x/y' -> { image, used }; image null while loading, false when missing
            artQueue: [], artInFlight: 0, artWanted: new Set(), artClock: 0, artDrawn: 0,
            redrawQueued: false,
            readout: host.querySelector('.pa-world-readout'),
            hover: null,            // what the pointer is over, for the readout to name again
            world: null,            // { mapIds: continent -> map id, zones: uiMapId -> zone map }
            areas: new Map(),       // area grid cell -> its zone's uiMapId, 0 for none
            zone: null,             // the zone last named
            areaTimer: 0,
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
            // A tool takes the click; else picking does, whatever is under it; else it selects a shape or a marker.
            onClick: info => {
                if (view.edit.tool) {
                    window.worldEdit.click(view, info);
                } else if (view.picking) {
                    picked(view, info);
                } else if (!window.worldEdit.click(view, info) && info.layer && info.layer.id === 'markers' && info.object) {
                    view.dotnet.invokeMethodAsync('OnMarkerClicked', info.object.id).catch(() => { });
                }
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
        loadWorld(view);
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
        // Points to frame instead of what the scene holds, such as a zone's corners.
        const frameOn = scene.frameOn && scene.frameOn.length >= 3 ? boundsOf(pathOf(scene.frameOn)) : null;

        if (scene.continent && scene.continent !== view.continent) {
            await loadContinent(view, scene.continent);
            if (!scene.frame && !frameOn) frameBounds(view, view.overview ? view.overview.bounds : null);
        }

        if (frameOn) {
            frameBounds(view, frameOn);
        } else if (scene.frame) {
            frameBounds(view, sceneBounds(view) || (view.overview ? view.overview.bounds : null));
        }

        render(view);
    }

    function boundsOf(positions) {
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        positions.forEach(p => {
            minX = Math.min(minX, p[0]); maxX = Math.max(maxX, p[0]);
            minY = Math.min(minY, p[1]); maxY = Math.max(maxY, p[1]);
        });
        return minX <= maxX ? [minX, minY, maxX, maxY] : null;
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
        view.tilted = !!tilted;
        view.deck.setProps({ views: viewOf(tilted) });
        moveTo(view, { rotationX: tilted ? TILTED : TOP_DOWN, rotationOrbit: tilted ? view.viewState.rotationOrbit : 0 });
        render(view);
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

    /** Shows the game's map art under the routes where the continent has it, or the relief alone. */
    function setArt(id, on) {
        const view = instances.get(id);
        if (!view) return;
        view.artOn = !!on;
        render(view);
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
        clearTimeout(view.areaTimer);
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
        view.areas = new Map();
        view.zone = null;
        view.artTiles = new Map();
        view.artQueue = [];
        markArt(view);
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
        const overview = { origin: data.origin, size, cells, byKey, bounds: cells.length ? [minX, minY, maxX, maxY] : null, image: null };
        cells.forEach(c => { c.color = shaded(overview, c); });
        overview.image = reliefImage(overview);
        return overview;
    }

    // How a slope is lit against level ground: it rises `east` and `north` yards a yard.
    function lightOf(east, north) {
        const e = east * EXAGGERATION;
        const n = north * EXAGGERATION;
        const light = ((-e * SUN[0]) - (n * SUN[1]) + SUN[2]) / Math.sqrt((e * e) + (n * n) + 1) / SUN[2];
        return Math.max(0.6, Math.min(1.3, light));
    }

    // A tile's height colour lit by its slope to the four tiles around it, a missing one counting
    // as level: the tilted view's steps.
    function shaded(o, cell) {
        const h = cell.height;
        const at = (tx, tz) => {
            const c = o.byKey.get(tx + ',' + tz);
            return c ? c.height : h;
        };
        // Tile x runs along wow y, so view east (-y) is tile x - 1; tile z runs along wow x, north.
        const light = lightOf((at(cell.tx - 1, cell.tz) - at(cell.tx + 1, cell.tz)) / (2 * o.size),
            (at(cell.tx, cell.tz + 1) - at(cell.tx, cell.tz - 1)) / (2 * o.size));
        return heightColor(h).map(c => Math.min(LIT_MAX, Math.round(c * light)));
    }

    // The relief as one picture for the flat view, north up: RELIEF_PIXELS to a tile's side, the
    // heights between tile middles smoothed (a Mitchell cubic) and each pixel lit by its own slope,
    // so a zone's hills read as hills, not as squares. Where no tile is baked - nothing walkable
    // there, or beyond the bake - the picture darkens, then clears, as smoothly. Column = maxTx - tx
    // (view X = -wow y, east to the right), row = maxTz - tz (view Y = wow x, north at the top).
    function reliefImage(o) {
        if (o.cells.length === 0) return null;
        let minTx = Infinity, maxTx = -Infinity, minTz = Infinity, maxTz = -Infinity;
        o.cells.forEach(c => {
            minTx = Math.min(minTx, c.tx); maxTx = Math.max(maxTx, c.tx);
            minTz = Math.min(minTz, c.tz); maxTz = Math.max(maxTz, c.tz);
        });
        const cols = maxTx - minTx + 1;
        const rows = maxTz - minTz + 1;
        const heights = new Float32Array(cols * rows).fill(NaN);
        const baked = new Float32Array(cols * rows);
        o.cells.forEach(c => {
            const i = ((maxTz - c.tz) * cols) + (maxTx - c.tx);
            heights[i] = c.height;
            baked[i] = 1;
        });
        // A pixel shows up to two tiles from a baked one, and its smoothing reaches two tiles further:
        // the unbaked tiles that close take the heights around them.
        for (let pass = 0; pass < 4; pass++) spread(heights, cols, rows);

        const k = RELIEF_PIXELS;
        const width = cols * k;
        const height = rows * k;
        const surface = upsample(heights, cols, rows, k, mitchell);
        const shown = upsample(baked, cols, rows, k, bSpline);

        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        const context = canvas.getContext('2d');
        const pixels = context.createImageData(width, height);
        const run = 2 * (o.size / k);
        for (let i = 0; i < width * height; i++) {
            const h = surface[i];
            if (shown[i] <= 0.01 || Number.isNaN(h)) continue;
            const x = i % width;
            // A neighbour beyond the picture, or too far from any baked tile to have a height, is level.
            const at = j => Number.isNaN(surface[j]) ? h : surface[j];
            const west = x > 0 ? at(i - 1) : h;
            const east = x < width - 1 ? at(i + 1) : h;
            const north = i >= width ? at(i - width) : h;
            const south = i + width < width * height ? at(i + width) : h;
            const light = lightOf((east - west) / run, (north - south) / run) * (UNBAKED + ((1 - UNBAKED) * ease(shown[i], 0.2, 0.8)));
            const c = rampOffset(h);
            pixels.data[i * 4] = Math.min(LIT_MAX, rampTable[c] * light);
            pixels.data[(i * 4) + 1] = Math.min(LIT_MAX, rampTable[c + 1] * light);
            pixels.data[(i * 4) + 2] = Math.min(LIT_MAX, rampTable[c + 2] * light);
            pixels.data[(i * 4) + 3] = 255 * ease(shown[i], 0, 0.3);
        }
        context.putImageData(pixels, 0, 0);
        return canvas;
    }

    // A grid of cols x rows samples, k pixels to a sample's side: each pixel a cubic over the four
    // samples nearest it (the end samples repeated beyond the ends), along the rows, then down the
    // columns.
    function upsample(values, cols, rows, k, kernel) {
        // A pixel lies the same fraction past a sample every k pixels: each phase's weights, once.
        const phases = [];
        for (let p = 0; p < k; p++) {
            const t = ((p + 0.5) / k) - 0.5;
            const weights = new Float64Array(4);
            kernel(t - Math.floor(t), weights);
            phases.push({ first: Math.floor(t) - 1, weights });
        }
        const line = (from, start, stride, count, to, toStart, toStride) => {
            for (let x = 0; x < count * k; x++) {
                const { first, weights } = phases[x % k];
                const s = Math.floor(x / k) + first;
                let sum = 0;
                for (let n = 0; n < 4; n++) {
                    sum += weights[n] * from[start + (Math.max(0, Math.min(count - 1, s + n)) * stride)];
                }
                to[toStart + (x * toStride)] = sum;
            }
        };
        const width = cols * k;
        const across = new Float32Array(width * rows);
        for (let r = 0; r < rows; r++) line(values, r * cols, 1, cols, across, r * width, 1);
        const result = new Float32Array(width * rows * k);
        for (let x = 0; x < width; x++) line(across, x, width, rows, result, x, width);
        return result;
    }

    // 0 below `from`, 1 above `to`, smoothly between.
    function ease(v, from, to) {
        const t = Math.max(0, Math.min(1, (v - from) / (to - from)));
        return t * t * (3 - (2 * t));
    }

    // Each unbaked tile beside one with a height takes the mean of the heights around it.
    function spread(heights, cols, rows) {
        const filled = heights.slice();
        for (let r = 0; r < rows; r++) {
            for (let c = 0; c < cols; c++) {
                if (!Number.isNaN(heights[(r * cols) + c])) continue;
                let sum = 0;
                let n = 0;
                for (let rr = Math.max(0, r - 1); rr <= Math.min(rows - 1, r + 1); rr++) {
                    for (let cc = Math.max(0, c - 1); cc <= Math.min(cols - 1, c + 1); cc++) {
                        const v = heights[(rr * cols) + cc];
                        if (!Number.isNaN(v)) {
                            sum += v;
                            n++;
                        }
                    }
                }
                if (n > 0) filled[(r * cols) + c] = sum / n;
            }
        }
        heights.set(filled);
    }

    // The four samples' weights for a point a fraction f past the second. Mitchell-Netravali (B = C =
    // 1/3) keeps a hill's shape without the tile-to-tile noise sharper cubics bring out (Catmull-Rom
    // does, in Winterspring's snow); the B-spline never passes beyond its samples, softer still: for
    // how much of a pixel's surroundings is baked.
    function mitchell(f, w) {
        w[0] = mitchellAt(1 + f);
        w[1] = mitchellAt(f);
        w[2] = mitchellAt(1 - f);
        w[3] = mitchellAt(2 - f);
    }

    function mitchellAt(d) {
        const B = 1 / 3;
        const C = 1 / 3;
        const d2 = d * d;
        const d3 = d2 * d;
        return d < 1
            ? (((12 - (9 * B) - (6 * C)) * d3) + ((-18 + (12 * B) + (6 * C)) * d2) + (6 - (2 * B))) / 6
            : (((-B - (6 * C)) * d3) + (((6 * B) + (30 * C)) * d2) + (((-12 * B) - (48 * C)) * d) + ((8 * B) + (24 * C))) / 6;
    }

    function bSpline(f, w) {
        const f2 = f * f;
        const f3 = f2 * f;
        w[0] = ((1 - f) ** 3) / 6;
        w[1] = ((3 * f3) - (6 * f2) + 4) / 6;
        w[2] = ((-3 * f3) + (3 * f2) + (3 * f) + 1) / 6;
        w[3] = f3 / 6;
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

    // --- the map art ---------------------------------------------------------------------------

    // The continent's art, when the service has it (not for Forever, nor where it is not downloaded).
    function artOf(view) {
        return view.world && view.continent ? view.world.arts.get(view.continent) || null : null;
    }

    // Whether the art is drawn now: it is wanted, the continent has it, and the view is flat. Tilted,
    // the relief's steps show the heights a flat picture would hide.
    function artShown(view) {
        return view.artOn && !view.tilted && !!artOf(view);
    }

    // The page's Map button shows only where there is art to show (the host's data-art).
    function markArt(view) {
        if (artOf(view)) view.host.dataset.art = '';
        else delete view.host.dataset.art;
    }

    // The art tiles the screen needs, at the zoom where a tile's pixel is about a screen pixel, and
    // under each not loaded yet the nearest coarser tile that is, so panning shows no holes. A tile
    // the service does not have (open sea) leaves the relief showing.
    function artLayers(view, under) {
        const art = artOf(view);
        const viewport = view.deck.getViewports()[0];
        if (!artShown(view) || !viewport) {
            view.artWanted = new Set();
            return [];
        }

        const ratio = window.devicePixelRatio || 1;
        const zoom = Math.max(art.minZoom, Math.min(art.maxZoom,
            Math.round(art.maxZoom + view.viewState.zoom + Math.log2(ratio * (ADT / 512)) + 0.2)));
        // Flat, the view looks straight down: the screen's corners on the ground bound what it shows.
        const corners = [[0, 0], [viewport.width, 0], [0, viewport.height], [viewport.width, viewport.height]].map(p => viewport.unproject(p));
        const xs = corners.map(c => c[0]);
        const ys = corners.map(c => c[1]);
        const size = artTileYards(art, zoom);
        const [left, top] = artCorner(art);
        const scale = Math.pow(2, zoom - art.maxZoom);
        const x0 = Math.max(0, Math.floor((Math.min(...xs) - left) / size));
        const x1 = Math.min(Math.ceil((art.width * scale) / ART_TILE) - 1, Math.floor((Math.max(...xs) - left) / size));
        const y0 = Math.max(0, Math.floor((top - Math.max(...ys)) / size));
        const y1 = Math.min(Math.ceil((art.height * scale) / ART_TILE) - 1, Math.floor((top - Math.min(...ys)) / size));

        const wanted = new Set();
        const fine = [];
        const coarse = new Map();
        for (let x = x0; x <= x1; x++) {
            for (let y = y0; y <= y1; y++) {
                const key = zoom + '/' + x + '/' + y;
                wanted.add(key);
                const tile = view.artTiles.get(key);
                if (tile && tile.image) {
                    fine.push(key);
                    continue;
                }
                if (tile && tile.image === false) continue;
                if (!tile) {
                    view.artTiles.set(key, { image: null, used: 0 });
                    view.artQueue.push(key);
                }
                for (let z = zoom - 1, cx = x >> 1, cy = y >> 1; z >= art.minZoom; z--, cx >>= 1, cy >>= 1) {
                    const parent = view.artTiles.get(z + '/' + cx + '/' + cy);
                    if (parent && parent.image) {
                        coarse.set(z + '/' + cx + '/' + cy, z);
                        break;
                    }
                }
            }
        }
        view.artWanted = wanted;
        pumpArt(view);

        // Coarser tiles first, under the finer ones.
        const keys = [...[...coarse.keys()].sort((a, b) => coarse.get(a) - coarse.get(b)), ...fine];
        const clock = ++view.artClock;
        view.artDrawn = keys.length;
        return keys.map(key => {
            const tile = view.artTiles.get(key);
            tile.used = clock;
            const [z, x, y] = key.split('/').map(Number);
            const [l, t] = artCorner(art);
            const s = artTileYards(art, z);
            return new deck.BitmapLayer({
                id: 'art-' + key, image: tile.image, bounds: [l + (x * s), t - ((y + 1) * s), l + ((x + 1) * s), t - (y * s)],
                parameters: under,
            });
        });
    }

    // The view X of the picture's left edge and the view Y of its top (view X = -wow y, Y = wow x).
    function artCorner(art) {
        return [(art.column * ADT) - HALF_WORLD, HALF_WORLD - (art.row * ADT)];
    }

    // The yards a tile spans at a zoom: an ADT is 512 pixels at the finest, twice as many yards a
    // pixel each zoom below.
    function artTileYards(art, zoom) {
        return ART_TILE * (ADT / 512) * Math.pow(2, art.maxZoom - zoom);
    }

    // Fetches the newest wanted tiles first, a few at a time; one the view panned away from is asked
    // for again when it is wanted.
    function pumpArt(view) {
        while (view.artInFlight < ART_FETCHES && view.artQueue.length > 0) {
            const key = view.artQueue.pop();
            if (!view.artWanted.has(key)) {
                view.artTiles.delete(key);
                continue;
            }
            const tiles = view.artTiles;
            const [z, x, y] = key.split('/');
            const image = new Image();
            image.src = fill(view.urls.art, { continent: view.continent, z, x, y });
            view.artInFlight++;
            image.decode()
                .then(() => { if (tiles.has(key)) tiles.get(key).image = image; })
                .catch(() => { if (tiles.has(key)) tiles.get(key).image = false; })
                .finally(() => {
                    view.artInFlight--;
                    if (view.disposed) return;
                    forgetArt(view);
                    redraw(view);
                    pumpArt(view);
                });
        }
    }

    // Keeps the tiles drawn last: beyond ART_KEPT, the least lately drawn go.
    function forgetArt(view) {
        if (view.artTiles.size <= ART_KEPT) return;
        const old = [...view.artTiles.entries()]
            .filter(([key, tile]) => tile.image !== null && !view.artWanted.has(key))
            .sort((a, b) => a[1].used - b[1].used);
        for (let i = 0; i < old.length && view.artTiles.size > ART_KEPT; i++) view.artTiles.delete(old[i][0]);
    }

    // Draws again on the next frame, once for everything that arrived meanwhile.
    function redraw(view) {
        if (view.redrawQueued) return;
        view.redrawQueued = true;
        requestAnimationFrame(() => {
            view.redrawQueued = false;
            render(view);
        });
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
                getFillColor: c => c.color,
                updateTriggers: { getPolygon: shown.size },
            }));

            // Flat, the relief is drawn smooth: its picture over the cells, which stay under it for the
            // heights a hover or a click picks. Tilted, the cells show their steps. Where a tile shows its
            // polygons, the dark under them is what is not walkable, as without the picture.
            if (!view.tilted && view.overview.image) {
                const under = { depthCompare: 'always', depthWriteEnabled: false };
                layers.push(new deck.BitmapLayer({
                    id: 'relief-image', image: view.overview.image, bounds: view.overview.bounds, parameters: under,
                }));
                layers.push(...artLayers(view, under));
                // Only while tiles show: a SolidPolygonLayer whose data becomes empty keeps drawing its
                // last polygons (deck.gl 9.4), which left a black square where tiles had shown. Over the
                // map art there is none: the art shows what is not walkable.
                if (shown.size && !artShown(view)) {
                    layers.push(new deck.SolidPolygonLayer({
                        id: 'relief-holes', data: detail.filter(c => shown.has(c.key)), material: false,
                        getPolygon: c => c.polygon, getFillColor: GROUND_CLEAR, parameters: under,
                    }));
                }
            }
        }

        // Over the map art a tile's polygons are see-through: the art shows what is there, the
        // polygons where a bot can walk.
        const mesh = artShown(view) ? ART_MESH_ALPHA : 255;
        for (const cell of detail) {
            const polygons = view.tiles.get(cell.key);
            if (!polygons) continue;
            layers.push(new deck.PolygonLayer({
                id: 'tile-' + cell.key, data: polygons, _full3d: true, pickable: '3d', material: false,
                stroked: true, filled: true, lineWidthUnits: 'pixels', getLineWidth: 1,
                getPolygon: p => p.polygon,
                getFillColor: p => [...(p.water ? WATER : heightColor(p.height)), mesh],
                getLineColor: OUTLINE,
                updateTriggers: { getFillColor: mesh },
            }));
        }

        const over = { depthCompare: 'always' };
        const labels = viewSpan(view) > DETAIL_SPAN ? apart(view, view.scene.labels) : [];
        // Zone names outlined in the dark, so they read over snow as over sea. The outline needs a
        // distance-field font; it reaches 0.75 x outlineWidth atlas pixels past a glyph (64 px glyphs:
        // about 1.7 px at 12), which the glyphs' buffer must hold.
        layers.push(new deck.TextLayer({
            id: 'labels', data: labels, getPosition: l => l.position, getText: l => l.text,
            getSize: LABEL_PX, sizeUnits: 'pixels', getColor: [226, 232, 240], fontWeight: 600,
            fontFamily: FONT, fontSettings: { sdf: true, buffer: 12, radius: 16 },
            outlineWidth: 12, outlineColor: [10, 14, 19, 210], parameters: over,
        }));

        // Shapes under the routes, so a route drawn through an area stays readable.
        layers.push(...window.worldEdit.shapeLayers(view));

        const solid = view.scene.routes.filter(r => !DASHED.has(r.kind));
        const dashed = view.scene.routes.filter(r => DASHED.has(r.kind));
        // Faint routes first, so the one that matters is drawn over them.
        solid.sort((a, b) => (b.kind === 'recent') - (a.kind === 'recent'));
        const faint = r => r.kind === 'recent';
        const dash = deck.PathStyleExtension ? [new deck.PathStyleExtension({ dash: true })] : [];
        // Every route over a dark casing a little wider, all casings under all routes, so a route reads
        // over any ground and where two cross. A dash's casing is a dash as long: dashes are measured in
        // widths, so the wider casing's are given in its own.
        layers.push(new deck.PathLayer({
            id: 'route-casings', data: solid, getPath: r => r.path, getColor: r => faint(r) ? CASING_FAINT : CASING,
            getWidth: r => (faint(r) ? 2 : 4) + CASING_PX, widthUnits: 'pixels', capRounded: true, jointRounded: true,
            parameters: over,
        }));
        layers.push(new deck.PathLayer({
            id: 'gap-casings', data: dashed, getPath: r => r.path, getColor: CASING,
            getWidth: 2 + CASING_PX, widthUnits: 'pixels', dashJustified: true, extensions: dash, parameters: over,
            getDashArray: GAP_DASH.map(d => (d * 2) / (2 + CASING_PX)),
        }));
        layers.push(new deck.PathLayer({
            id: 'routes', data: solid, getPath: r => r.path, getColor: r => KINDS[r.kind] || KINDS.point,
            getWidth: r => faint(r) ? 2 : 4, widthUnits: 'pixels', capRounded: true, jointRounded: true,
            pickable: true, parameters: over,
        }));
        layers.push(new deck.PathLayer({
            id: 'gaps', data: dashed, getPath: r => r.path, getColor: r => KINDS[r.kind] || KINDS.point,
            getWidth: 2, widthUnits: 'pixels', getDashArray: GAP_DASH, dashJustified: true, pickable: true,
            extensions: dash, parameters: over,
        }));
        layers.push(new deck.ScatterplotLayer({
            id: 'markers', data: view.scene.markers, getPosition: m => m.position,
            getFillColor: m => KINDS[m.kind] || KINDS.point,
            getLineColor: m => SMALL.has(m.kind) ? [10, 14, 19, 200] : [255, 255, 255, 230], stroked: true,
            radiusUnits: 'pixels', getRadius: m => SMALL.has(m.kind) ? 4.5 : 7,
            lineWidthUnits: 'pixels', getLineWidth: m => SMALL.has(m.kind) ? 1.2 : 2, pickable: true, parameters: over,
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

    // The names that fit on the screen as it is drawn now, larger zones first: one that would overlap
    // a name already placed waits for a closer look. A name's box is its text measured in the labels'
    // font, around its anchor.
    let measure = null;
    function apart(view, labels) {
        const viewport = view.deck.getViewports()[0];
        if (!viewport || labels.length < 2) return labels;
        if (!measure) {
            measure = document.createElement('canvas').getContext('2d');
            measure.font = '600 ' + LABEL_PX + 'px ' + FONT;
        }
        const placed = [];
        return [...labels].sort((a, b) => (b.size || 0) - (a.size || 0)).filter(l => {
            const [x, y] = viewport.project(l.position);
            const w = (measure.measureText(l.text).width / 2) + LABEL_GAP;
            const h = LABEL_PX / 2;
            if (placed.some(b => x - w < b[2] && b[0] < x + w && y - h < b[3] && b[1] < y + h)) return false;
            placed.push([x - w, y - h, x + w, y + h]);
            return true;
        });
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

    // Where the pointer is, at the view's foot: the zone and its map coordinates as a player reads
    // them, world yards, and the ground's height.
    function showReadout(view, info) {
        if (!view.readout) return;
        view.hover = info.coordinate ? info : null;
        if (!info.coordinate) {
            view.readout.textContent = '';
            return;
        }
        const w = fromView(info.coordinate);
        const layer = info.layer ? info.layer.id : '';
        // A tile's polygons have the ground's own height; the relief only the tile's.
        const height = layer.startsWith('tile-') ? (info.object && info.object.water ? 'water, height ' : 'height ') + w.z.toFixed(1)
            : (layer === 'relief' ? 'tile height ' + w.z.toFixed(0) : null);
        const where = whereIs(view, w.x, w.y);
        const place = document.createElement('span');
        place.className = 'pa-world-place';
        place.textContent = where ? where + '   ' : '';
        view.readout.replaceChildren(place, ['x ' + w.x.toFixed(1) + '  y ' + w.y.toFixed(1), height].filter(part => part).join('   '));
    }

    // The continents' map ids and every zone map, for naming the pointer's place; without them the
    // readout gives coordinates only.
    async function loadWorld(view) {
        try {
            const response = await fetch(view.urls.world);
            if (!response.ok) return;
            const world = await response.json();
            view.world = {
                mapIds: new Map(world.continents.map(c => [c.name, c.mapId])),
                arts: new Map(world.continents.filter(c => c.art).map(c => [c.name, c.art])),
                zones: new Map(world.zones.map(z => [z.uiMapId, z])),
            };
            markArt(view);
            render(view);
        } catch (e) {
            window.pathingUi.log('Zone names for the pointer could not be read: ' + e.message);
        }
    }

    // The zone at a world point as the service's area grid has it (a town in its zone), with the
    // point in the zone map's coordinates: "Durotar 52.3, 44.1". The grid is asked once the pointer
    // rests, and the answer kept for its cell (ZoneLookup's); till then the zone last named stands in.
    function whereIs(view, x, y) {
        const mapId = view.world ? view.world.mapIds.get(view.continent) : undefined;
        if (mapId === undefined) return null;
        const cx = Math.floor(x / AREA_CELL);
        const cy = Math.floor(y / AREA_CELL);
        const key = cx + ',' + cy;
        clearTimeout(view.areaTimer);
        if (view.areas.has(key)) {
            view.zone = view.world.zones.get(view.areas.get(key)) || null;
        } else {
            view.areaTimer = setTimeout(() => lookUp(view, mapId, key, (cx + 0.5) * AREA_CELL, (cy + 0.5) * AREA_CELL), AREA_REST_MS);
        }
        return view.zone ? onMap(view.zone, x, y) : null;
    }

    async function lookUp(view, mapId, key, x, y) {
        const continent = view.continent;
        try {
            const response = await fetch(fill(view.urls.area, { mapId, x: x.toFixed(1), y: y.toFixed(1) }));
            if (!response.ok) return;
            const area = await response.json();
            if (view.disposed || view.continent !== continent) return;
            view.areas.set(key, area.uiMapId || 0);
        } catch {
            return;   // asked again when the pointer next rests there
        }
        if (view.hover) showReadout(view, view.hover);
    }

    // A world point in a zone map's coordinates, 0 to 100 (ZoneMath.ToMap): map x runs along wow y,
    // map y along wow x. Off the map, the zone's name alone.
    function onMap(zone, x, y) {
        const mapX = 100 - (((y - zone.locRight) * 100) / (zone.locLeft - zone.locRight));
        const mapY = 100 - (((x - zone.locBottom) * 100) / (zone.locTop - zone.locBottom));
        return mapX >= 0 && mapX <= 100 && mapY >= 0 && mapY <= 100
            ? zone.name + ' ' + mapX.toFixed(1) + ', ' + mapY.toFixed(1)
            : zone.name;
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
        }
        // The ground has none: the readout says where the pointer is and how high.
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
                art: artShown(v) ? v.artDrawn : null,
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

    window.worldView = { create, setScene, setTool, setTilt, setArt, fit, centerOn, setPicking, dispose, describe, screenOf };
})();
