// Drawing and editing shapes on the world view (world-view.js hands its pointer and keys here, and
// draws the layers this returns). The page names the tool and owns the shapes: a point, a line, a
// polygon, points, a rectangle or a circle, each with a kind (its look) and whether it can be
// edited. The pointer's work is local while it lasts - a draft being drawn, a handle being dragged -
// and the finished geometry goes to the page, which sends the shapes back in its next scene.
//
// Tools: a point is one click (on a marker, it says which, for the page to snap to); a line, a
// polygon or points take a click per vertex and end with Enter, a click on the last vertex (a
// double-click) or, for a polygon, on the first; a rectangle takes two corners; a circle its centre,
// then a point on its rim; a path is drawn freehand, pressed and dragged along the way. Escape drops
// the draft, then the tool; Backspace takes the last vertex back.
//
// Handles, on the selected shape when it can be edited: drag a vertex to move it, drag the dot
// between two vertices to insert one, right-click a vertex (or press Delete after touching it) to
// remove it. A circle has its centre and a rim handle; a rectangle its corners.
//
// Heights: a point placed on a tile's polygons has the ground's height; one placed on the relief
// (one height per tile, zoomed out) is sent with height 0, which the Pathing Service reads as "find
// the ground here". Such a point is marked unknown while the pointer holds it.

(function () {
    'use strict';

    const COLORS = {
        waypoints: [235, 240, 246],
        path: [210, 168, 255],
        road: [63, 185, 80],
        'danger-avoid': [232, 163, 61],
        'danger-block': [230, 72, 64],
        area: [88, 166, 255],
        poi: [57, 197, 207],
        mark: [217, 112, 214],
        'adt-whole': [49, 214, 90],
        'adt-part': [214, 165, 49],
        'adt-chosen': [240, 178, 50],
        resident: [57, 197, 207],
    };
    // Kinds that outline the ground in their hundreds, filled lightly so the ground stays readable.
    const FILL = { 'adt-whole': 10, 'adt-part': 22, 'adt-chosen': 60, resident: 40 };
    const DEFAULT_COLOR = [235, 240, 246];
    const DRAFT_COLOR = [240, 178, 50];
    const CASING = [8, 12, 16, 210];   // the dark under a line, as under a route (world-view.js)
    const CASING_PX = 3;
    const MIN_VERTICES = { line: 2, polygon: 3, points: 1 };
    const FINISH_PX = 9;        // a click this close to the draft's last (or a polygon's first) vertex finishes it
    const STROKE_PX = 4;        // a freehand path takes a point each time the pointer has moved this far
    const MAX_NUMBERED = 60;    // waypoints beyond this many are numbered first and last only
    const FEW_NUMBERED = 12;    // this many or fewer are numbered at any zoom; more only close in
    const FONT = 'ui-sans-serif, system-ui, Segoe UI, sans-serif';

    function colorOf(kind, alpha) {
        const c = COLORS[kind] || DEFAULT_COLOR;
        return alpha === undefined ? c : [c[0], c[1], c[2], alpha];
    }

    /** The editing state of a view. */
    function attach(view, helpers) {
        view.edit = {
            helpers,             // { toView, fromView, lift, groundAt(x, y, info), closeIn(), emit(method, ...args), render() }
            tool: null,
            draft: [],           // view coordinates
            cursor: null,
            shapes: [],
            selected: null,
            overHandle: false,
            drag: null,          // { shape, handle }
            stroke: false,       // a freehand path is being drawn: the draft is its points
            active: null,        // { id, index }: the vertex last touched, for Delete
        };
    }

    // --- from the page -------------------------------------------------------------------------

    function setShapes(view, shapes, selected) {
        const edit = view.edit;
        const h = edit.helpers;
        edit.shapes = (shapes || []).map(s => {
            const vertices = [];
            for (let i = 0; i + 2 < s.points.length; i += 3) {
                vertices.push(h.toView(s.points[i], s.points[i + 1], s.points[i + 2] + h.lift));
            }
            return { ...s, vertices };
        });
        edit.selected = selected || null;
        edit.drag = null;
        if (edit.active && !edit.shapes.some(s => s.id === edit.active.id)) edit.active = null;
    }

    // The same tool again changes nothing: the view sets it the moment its button is pressed, and the
    // page's own word on it follows, which must not drop a corner drawn meanwhile.
    function setTool(view, tool) {
        const edit = view.edit;
        if (edit.tool === (tool || null)) return;
        edit.tool = tool || null;
        edit.draft = [];
        edit.cursor = null;
        edit.helpers.render();
    }

    // --- layers --------------------------------------------------------------------------------

    /** The shapes: drawn under the page's routes and markers. */
    function shapeLayers(view) {
        const edit = view.edit;
        const over = { depthCompare: 'always' };
        const result = [];
        const isSelected = s => s.id === edit.selected;

        const areas = edit.shapes.filter(s => (s.type === 'polygon' || s.type === 'rectangle') && s.vertices.length > 1);
        result.push(new deck.PolygonLayer({
            id: 'shape-areas', data: areas, pickable: true, stroked: true, filled: true, material: false,
            getPolygon: s => s.type === 'rectangle' ? rectangle(s.vertices) : s.vertices,
            getFillColor: s => colorOf(s.kind, FILL[s.kind] ?? (isSelected(s) ? 70 : 45)),
            getLineColor: s => colorOf(s.kind, FILL[s.kind] !== undefined && !isSelected(s) ? 130 : 235),
            lineWidthUnits: 'pixels', getLineWidth: s => isSelected(s) ? 3 : (FILL[s.kind] !== undefined ? 1 : 2),
            updateTriggers: { getFillColor: edit.selected, getLineWidth: edit.selected },
            parameters: over,
        }));

        const circles = edit.shapes.filter(s => s.type === 'circle' && s.vertices.length > 0);
        result.push(new deck.ScatterplotLayer({
            id: 'shape-circles', data: circles, pickable: true, stroked: true, filled: true,
            getPosition: s => s.vertices[0], getRadius: s => s.radius, radiusUnits: 'common',
            getFillColor: s => colorOf(s.kind, isSelected(s) ? 70 : 45),
            getLineColor: s => colorOf(s.kind, 235),
            lineWidthUnits: 'pixels', getLineWidth: s => isSelected(s) ? 3 : 2,
            updateTriggers: { getFillColor: edit.selected, getLineWidth: edit.selected },
            parameters: over,
        }));

        // Waypoints are joined by the legs planned between them, not by a line of their own.
        const lines = edit.shapes.filter(s => (s.type === 'line' || (s.type === 'points' && s.kind !== 'waypoints')) && s.vertices.length > 1);
        const lineWidth = s => (isSelected(s) ? 3 : 2) + (s.type === 'line' ? 1 : 0);
        // A line over a dark casing, as a route is, so a road reads over any ground.
        result.push(new deck.PathLayer({
            id: 'line-casings', data: lines.filter(s => s.type === 'line'), getPath: s => s.vertices,
            getColor: CASING, widthUnits: 'pixels', getWidth: s => lineWidth(s) + CASING_PX,
            capRounded: true, jointRounded: true, updateTriggers: { getWidth: edit.selected },
            parameters: over,
        }));
        result.push(new deck.PathLayer({
            id: 'shape-lines', data: lines, pickable: true,
            getPath: s => s.vertices, getColor: s => colorOf(s.kind, s.type === 'points' ? 130 : 235),
            widthUnits: 'pixels', getWidth: lineWidth,
            capRounded: true, jointRounded: true,
            getDashArray: s => s.type === 'points' ? [4, 4] : [0, 0], dashJustified: true,
            extensions: deck.PathStyleExtension ? [new deck.PathStyleExtension({ dash: true })] : [],
            updateTriggers: { getWidth: edit.selected },
            parameters: over,
        }));

        // Points stand on their own: a dot each (the selected shape's are its handles).
        const dots = [];
        edit.shapes.filter(s => (s.type === 'points' || s.type === 'point') && !(isSelected(s) && s.editable)).forEach(s =>
            s.vertices.forEach((p, i) => dots.push({ shape: s, index: i, position: p })));
        result.push(new deck.ScatterplotLayer({
            id: 'shape-dots', data: dots, pickable: true, getPosition: d => d.position,
            getFillColor: d => colorOf(d.shape.kind), getLineColor: [10, 14, 19, 230], stroked: true,
            radiusUnits: 'pixels', getRadius: d => d.shape.type === 'point' ? 6 : 4.5,
            lineWidthUnits: 'pixels', getLineWidth: 1.5, parameters: over,
        }));

        // Names show close in, or on the selected shape: zoomed out over a continent they only pile up.
        const close = edit.helpers.closeIn();
        const labels = [];
        const numbers = [];
        edit.shapes.forEach(s => {
            if (s.label && s.vertices.length > 0 && (close || isSelected(s))) labels.push({ text: s.label, position: labelPosition(s) });
            if (s.type === 'points' && s.kind === 'waypoints') {
                const n = s.vertices.length;
                s.vertices.forEach((p, i) => {
                    if (n <= FEW_NUMBERED || (close && n <= MAX_NUMBERED) || i === 0 || i === n - 1) numbers.push({ text: String(i + 1), position: p });
                });
            }
        });
        result.push(new deck.TextLayer({
            id: 'shape-labels', data: labels, getPosition: l => l.position, getText: l => l.text,
            getSize: 13, sizeUnits: 'pixels', getColor: [230, 237, 243], background: true,
            getBackgroundColor: [16, 22, 30, 215], backgroundPadding: [4, 2], fontWeight: 600,
            fontFamily: FONT, parameters: over,
        }));
        // Waypoint numbers outlined beside their dot, as zone names are, rather than boxed over the
        // route: a box hid the dots and legs next to it.
        result.push(new deck.TextLayer({
            id: 'shape-numbers', data: numbers, getPosition: l => l.position, getText: l => l.text,
            getSize: 11, sizeUnits: 'pixels', getColor: [235, 240, 246], getTextAnchor: 'start',
            getPixelOffset: [7, -9], fontWeight: 700, fontFamily: FONT,
            fontSettings: { sdf: true, buffer: 12, radius: 16 }, outlineWidth: 12, outlineColor: [10, 14, 19, 230],
            parameters: over,
        }));

        return result;
    }

    // An area's name sits in its middle; a line's or a point's at its first vertex.
    function labelPosition(s) {
        if (s.type === 'circle') return s.vertices[0];
        if (s.type !== 'polygon' && s.type !== 'rectangle') return s.vertices[0];
        const v = s.type === 'rectangle' ? rectangle(s.vertices) : s.vertices;
        const sum = v.reduce((a, p) => [a[0] + p[0], a[1] + p[1], a[2] + p[2]], [0, 0, 0]);
        return sum.map(c => c / v.length);
    }

    // Handles show on the selected shape, except while a shape is being drawn: its corners go where
    // they are clicked, handles or not. Adding points leaves them, so a point can be dragged meanwhile.
    function editing(edit) {
        const selected = edit.shapes.find(s => s.id === edit.selected);
        return selected && selected.editable && (!edit.tool || edit.tool === 'point') ? selected : null;
    }

    /** The selected shape's handles and the draft: drawn over everything. */
    function overlayLayers(view) {
        const edit = view.edit;
        const over = { depthCompare: 'always' };
        const selected = editing(edit);
        const result = [];

        if (selected) {
            result.push(new deck.ScatterplotLayer({
                id: 'handles', data: handlesOf(selected), pickable: true, getPosition: d => d.position,
                getFillColor: d => d.kind === 'mid' ? [235, 240, 246, 110] : [235, 240, 246],
                getLineColor: d => d.kind === 'mid' ? [10, 14, 19, 110] : colorOf(selected.kind === 'waypoints' ? 'area' : selected.kind),
                stroked: true, radiusUnits: 'pixels', getRadius: d => d.kind === 'mid' ? 4 : 6.5,
                lineWidthUnits: 'pixels', getLineWidth: 2.5, parameters: over,
            }));
        }

        if (edit.tool && (edit.draft.length > 0 || edit.cursor)) {
            result.push(...draftLayers(edit, over));
        }

        return result;
    }

    function draftLayers(edit, over) {
        const points = edit.cursor ? [...edit.draft, edit.cursor] : edit.draft;
        const result = [];
        if (edit.tool === 'rectangle' && edit.draft.length === 1 && edit.cursor) {
            result.push(new deck.PolygonLayer({
                id: 'draft-area', data: [rectangle([edit.draft[0], edit.cursor])], getPolygon: p => p, material: false,
                getFillColor: [...DRAFT_COLOR, 40], getLineColor: DRAFT_COLOR, lineWidthUnits: 'pixels', getLineWidth: 2,
                parameters: over,
            }));
        } else if (edit.tool === 'circle' && edit.draft.length === 1 && edit.cursor) {
            result.push(new deck.ScatterplotLayer({
                id: 'draft-area', data: [edit.draft[0]], getPosition: p => p, radiusUnits: 'common',
                getRadius: distance2(edit.draft[0], edit.cursor), stroked: true, filled: true,
                getFillColor: [...DRAFT_COLOR, 40], getLineColor: DRAFT_COLOR, lineWidthUnits: 'pixels', getLineWidth: 2,
                parameters: over,
            }));
        } else if (points.length > 1 && edit.tool !== 'point') {
            const path = edit.tool === 'polygon' && points.length > 2 ? [...points, points[0]] : points;
            result.push(new deck.PathLayer({
                id: 'draft-line', data: [path], getPath: p => p, getColor: DRAFT_COLOR, widthUnits: 'pixels', getWidth: 2,
                parameters: over,
            }));
        }

        // A freehand path is a line, not a point every few pixels.
        result.push(new deck.ScatterplotLayer({
            id: 'draft-points', data: edit.tool === 'path' ? [] : edit.draft, getPosition: p => p, getFillColor: DRAFT_COLOR,
            getLineColor: [10, 14, 19], stroked: true, radiusUnits: 'pixels', getRadius: 5,
            lineWidthUnits: 'pixels', getLineWidth: 1.5, parameters: over,
        }));
        return result;
    }

    function handlesOf(shape) {
        const v = shape.vertices;
        const handles = [];
        if (shape.type === 'circle') {
            if (v.length > 0) {
                handles.push({ kind: 'center', index: 0, position: v[0] });
                handles.push({ kind: 'rim', index: 0, position: [v[0][0] + shape.radius, v[0][1], v[0][2]] });
            }
            return handles;
        }

        if (shape.type === 'rectangle') {
            rectangle(v).forEach((p, i) => handles.push({ kind: 'corner', index: i, position: p }));
            return handles;
        }

        v.forEach((p, i) => handles.push({ kind: 'vertex', index: i, position: p }));
        if (shape.type === 'point') return handles;
        const closed = shape.type === 'polygon';
        for (let i = 0; i < v.length - 1 + (closed && v.length > 2 ? 1 : 0); i++) {
            const a = v[i];
            const b = v[(i + 1) % v.length];
            handles.push({ kind: 'mid', index: i, position: [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2] });
        }
        return handles;
    }

    // Two opposite corners -> the four, in order.
    function rectangle(corners) {
        if (corners.length < 2) return corners;
        const [a, b] = corners;
        const z = (a[2] + b[2]) / 2;
        return [[a[0], a[1], z], [b[0], a[1], z], [b[0], b[1], z], [a[0], b[1], z]];
    }

    function distance2(a, b) {
        return Math.hypot(a[0] - b[0], a[1] - b[1]);
    }

    // --- the pointer ---------------------------------------------------------------------------

    /** The ground under the pointer, in view coordinates; null off the map. */
    function positionOf(edit, info) {
        if (info.x === undefined || info.x < 0) return null;
        return edit.helpers.groundAt(info.x, info.y, info);
    }

    /** A click: true when the tools took it. */
    function click(view, info) {
        const edit = view.edit;
        const layer = info.layer ? info.layer.id : null;

        if (edit.tool) {
            if (layer === 'handles') return true;
            const p = positionOf(edit, info);
            if (!p) return true;

            // A path is drawn by dragging; a click alone draws nothing.
            if (edit.tool === 'path') return true;

            if (edit.tool === 'point') {
                emitDrawn(edit, 'point', [p], 0, layer === 'markers' && info.object ? info.object.id : null);
                return true;
            }

            if (edit.tool === 'rectangle' || edit.tool === 'circle') {
                edit.draft.push(p);
                if (edit.draft.length === 2) {
                    const [a, b] = edit.draft;
                    if (edit.tool === 'circle') emitDrawn(edit, 'circle', [a], distance2(a, b));
                    else emitDrawn(edit, 'rectangle', [a, b], 0);
                    edit.draft = [];
                }
                edit.helpers.render();
                return true;
            }

            // line, polygon, points: a click on the last vertex (or a polygon's first) finishes.
            const min = MIN_VERTICES[edit.tool] || 1;
            if (edit.draft.length >= min && (near(view, info, edit.draft[edit.draft.length - 1]) ||
                (edit.tool === 'polygon' && near(view, info, edit.draft[0])))) {
                finish(edit);
            } else {
                edit.draft.push(p);
                edit.helpers.render();
            }
            return true;
        }

        if (layer && layer.startsWith('shape-') && info.object) {
            const shape = info.object.shape || info.object;
            if (shape.id && shape.id !== edit.selected) edit.helpers.emit('OnShapeSelected', shape.id);
            return true;
        }

        if (layer === 'handles') return true;

        // A click beside every shape lets the selection go, and is still the page's click.
        if (edit.selected) edit.helpers.emit('OnShapeSelected', null);
        return false;
    }

    function near(view, info, point, px) {
        const viewport = view.deck.getViewports()[0];
        if (!viewport || info.x === undefined) return false;
        const [x, y] = viewport.project(point);
        return Math.hypot(x - info.x, y - info.y) <= (px || FINISH_PX);
    }

    // The draft as drawn so far, sent; a freehand stroke ends with it, the rest of the drag drawing nothing.
    function finish(edit) {
        const min = MIN_VERTICES[edit.tool] || 1;
        if (edit.draft.length >= min) emitDrawn(edit, edit.tool, edit.draft, 0);
        edit.draft = [];
        edit.stroke = false;
        edit.helpers.render();
    }

    function emitDrawn(edit, tool, points, radius, marker) {
        edit.helpers.emit('OnShapeDrawn', tool, flat(edit, points), round(radius), marker || null);
    }

    // World x, y, z triples, to a tenth of a yard; an unknown height is sent as 0.
    function flat(edit, points) {
        const h = edit.helpers;
        const result = [];
        points.forEach(p => {
            const w = h.fromView(p);
            result.push(round(w.x), round(w.y), p.unknown ? 0 : round(Math.max(-5000, (p[2] || 0) - h.lift)));
        });
        return result;
    }

    function round(v) {
        return Math.round(v * 10) / 10;
    }

    /** The pointer moved: the draft follows it, and over a handle the cursor says it can be dragged. */
    function hover(view, info) {
        const edit = view.edit;
        if (edit.tool && !edit.drag) {
            edit.cursor = positionOf(edit, info);
            if (edit.draft.length > 0) edit.helpers.render();
        }
        edit.overHandle = !!(info.layer && info.layer.id === 'handles' && info.object);
    }

    /** A drag began: true when it is a handle's or draws a path, so the map must not pan. */
    function dragStart(view, info) {
        const edit = view.edit;
        if (edit.tool === 'path') {
            const p = positionOf(edit, info);
            if (!p) return false;
            edit.stroke = true;
            edit.draft = [p];
            edit.cursor = null;
            edit.helpers.render();
            return true;
        }

        if (!info.layer || info.layer.id !== 'handles' || !info.object) return false;
        const shape = editing(edit);
        if (!shape) return false;

        let handle = info.object;
        if (handle.kind === 'mid') {
            // The dot between two vertices becomes a new vertex there.
            shape.vertices.splice(handle.index + 1, 0, handle.position.slice());
            handle = { kind: 'vertex', index: handle.index + 1 };
        }
        edit.drag = { shape, handle };
        edit.active = handle.kind === 'vertex' ? { id: shape.id, index: handle.index } : null;
        return true;
    }

    function drag(view, info) {
        const edit = view.edit;
        if (edit.stroke) {
            const p = info.x === undefined ? null : edit.helpers.groundAt(info.x, info.y, null);
            if (p && (edit.draft.length === 0 || !near(view, info, edit.draft[edit.draft.length - 1], STROKE_PX))) {
                edit.draft.push(p);
                edit.helpers.render();
            }
            return true;
        }

        if (!edit.drag) return false;
        // The pointer is on the handle itself: the ground is looked up under it.
        const p = info.x === undefined ? null : edit.helpers.groundAt(info.x, info.y, null);
        if (!p) return true;

        const { shape, handle } = edit.drag;
        const v = shape.vertices;
        if (handle.kind === 'vertex') {
            v[handle.index] = p;
        } else if (handle.kind === 'center') {
            v[0] = p;
        } else if (handle.kind === 'rim') {
            shape.radius = Math.max(1, distance2(v[0], p));
        } else if (handle.kind === 'corner') {
            // The corner opposite the dragged one stays.
            const corners = rectangle(v);
            shape.vertices = [corners[(handle.index + 2) % 4], p];
            edit.drag.handle = { kind: 'corner', index: 2 };
        }
        edit.helpers.render();
        return true;
    }

    function dragEnd(view) {
        const edit = view.edit;
        if (edit.stroke) {
            edit.stroke = false;
            if (edit.draft.length > 1) emitDrawn(edit, 'path', edit.draft, 0);
            edit.draft = [];
            edit.helpers.render();
            return true;
        }

        if (!edit.drag) return false;
        const { shape } = edit.drag;
        edit.drag = null;
        emitEdited(edit, shape);
        return true;
    }

    function emitEdited(edit, shape) {
        edit.helpers.emit('OnShapeEdited', shape.id, flat(edit, shape.vertices), round(shape.radius || 0));
    }

    /** A right-click: takes the draft's last vertex back, or removes the vertex under the pointer. */
    function contextClick(view, x, y) {
        const edit = view.edit;
        if (edit.tool && edit.draft.length > 0) {
            edit.draft.pop();
            edit.helpers.render();
            return true;
        }

        const picked = view.deck.pickObject({ x, y, radius: 4, layerIds: ['handles'] });
        if (!picked || !picked.object || picked.object.kind !== 'vertex') return false;
        removeVertex(edit, edit.selected, picked.object.index);
        return true;
    }

    function removeVertex(edit, id, index) {
        const shape = edit.shapes.find(s => s.id === id);
        if (!shape || !shape.editable) return;
        const min = MIN_VERTICES[shape.type];
        if (min === undefined || shape.vertices.length <= min) return;
        shape.vertices.splice(index, 1);
        edit.active = null;
        edit.helpers.render();
        emitEdited(edit, shape);
    }

    /** A key on the map: true when the tools took it. */
    function key(view, event) {
        const edit = view.edit;
        switch (event.key) {
            case 'Escape':
                if (edit.draft.length > 0) {
                    edit.stroke = false;
                    edit.draft = [];
                    edit.helpers.render();
                } else if (edit.tool) {
                    setTool(view, null);
                    edit.helpers.emit('OnToolCleared');
                } else {
                    return false;
                }
                return true;
            case 'Enter':
                if (!edit.tool || edit.draft.length === 0) return false;
                finish(edit);
                return true;
            case 'Backspace':
                if (!edit.tool || edit.draft.length === 0) return false;
                edit.draft.pop();
                edit.helpers.render();
                return true;
            case 'Delete':
                if (!edit.active) return false;
                removeVertex(edit, edit.active.id, edit.active.index);
                return true;
            default:
                return false;
        }
    }

    function cursor(view) {
        const edit = view.edit;
        if (edit.drag) return 'grabbing';
        if (edit.overHandle) return 'move';
        return edit.tool ? 'crosshair' : null;
    }

    /** What the tools hold, for a browser check: the selected shape's handles where they are on the screen. */
    function describe(view) {
        const edit = view.edit;
        const selected = editing(edit);
        const viewport = view.deck.getViewports()[0];
        const handles = selected && viewport
            ? handlesOf(selected).map(h => {
                const [x, y] = viewport.project(h.position);
                return { kind: h.kind, index: h.index, x, y };
            })
            : [];
        return {
            tool: edit.tool, draft: edit.draft.length, selected: edit.selected,
            shapes: edit.shapes.map(s => s.id + ':' + s.type + ':' + s.vertices.length),
            handles,
        };
    }

    window.worldEdit = {
        attach, setShapes, setTool, shapeLayers, overlayLayers, click, hover, dragStart, drag, dragEnd,
        contextClick, key, cursor, describe,
    };
})();
