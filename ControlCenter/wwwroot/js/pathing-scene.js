// The Control Center's 3D route view, driven by the Pathing Service's watch hub (forwarded at
// /watchHub; methods are Contracts.Pathing.WatchHubMethods).
//
// It shows the navmesh tiles the planner's lanes hold and every route the service plans. A view
// opened late asks the hub to replay what the others see; nothing on the server is reset for it.
// Terrain - the game geometry a tile was baked from - is fetched on demand for the tiles nearest
// the camera, and only where the service has the game files.
//
// Babylon.js is ~8.6 MB, so it is fetched the first time a 3D page opens rather than on every
// page. Each page visit creates one engine on its canvas and disposes it on leave, hub
// connection included. Everything lives on window.pathingScene - no loose globals - because
// the map scripts own the global removeMesh/clear.

(function () {
    'use strict';

    const BABYLON_URL = 'https://cdn.babylonjs.com/babylon.js';
    const TEXTURE_URL = 'https://www.babylonjs-playground.com/textures/';
    // Terrain kinds, in the order the hub sends them: terrain, liquid, objects (WMO), models (M2).
    const TERRAIN_TEXTURES = ['grass.png', 'waterbump.png', 'floor.png', 'ground.jpg'];
    const TERRAIN_KINDS = TERRAIN_TEXTURES.length;
    const MAX_TERRAIN_TILES = 25;   // PathingService BakeAdmin.MaxTerrainTiles
    const NAVMESH_TILE = /^navmesh_(.+)_(-?\d+)_(-?\d+)_[gw]$/;
    const div = 10.0;          // world yards per scene unit
    const CAMERA_SLOW = 0.01;
    const CAMERA_NORMAL = 0.1;
    const CAMERA_FAST = 1;
    const INERTIA_EPSILON = 0.0001;

    let engine = null;
    let scene = null;
    let camera = null;
    let terrainMaterials = [];
    let terrainRoots = [];
    let navmeshRoot = null;
    let terrainOn = false;
    let dirty = true;
    let cameraPositionSet = false;
    let teardown = [];
    let connection = null;

    function requestRender() { dirty = true; }

    function listen(target, type, handler, options) {
        target.addEventListener(type, handler, options);
        teardown.push(() => target.removeEventListener(type, handler, options));
    }

    function isTyping(e) {
        const t = e.target;
        return t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
    }

    // wow (x, y, z) -> babylon (x, z, y), scaled
    function toScene(x, y, z, lift) {
        return new BABYLON.Vector3(x / div, (z / div) + (lift || 0), y / div);
    }

    // Flat wow x, y, z triples -> scene positions.
    function scenePositions(flat) {
        const positions = new Float32Array(flat.length);
        for (let i = 0; i < flat.length; i += 3) {
            positions[i] = flat[i] / div;
            positions[i + 1] = flat[i + 2] / div;
            positions[i + 2] = flat[i + 1] / div;
        }
        return positions;
    }

    // Contracts.Pathing.WatchColor
    function colour(code) {
        switch (code) {
            case 1: return BABYLON.Color3.Red();
            case 2: return BABYLON.Color3.Green();
            case 3: return BABYLON.Color3.Blue();
            case 4: return BABYLON.Color3.Teal();
            case 5: return BABYLON.Color3.White();
            case 6: return new BABYLON.Color3(1, 0.6, 0);
            case 7: return BABYLON.Color3.Yellow();
            case 8: return BABYLON.Color3.Black();
            case 9: return BABYLON.Color3.Magenta();
            default: return BABYLON.Color3.White();
        }
    }

    function markerHeight(code) {
        return code === 2 ? 5 / div : 1 / div;
    }

    function setCamera(pos, look, height) {
        if (!scene || cameraPositionSet) return;
        cameraPositionSet = true;
        camera.position = toScene(pos.x, pos.y, pos.z, height || 0);
        camera.setTarget(toScene(look.x, look.y, look.z));
        requestRender();
    }

    function removeMesh(name) {
        if (!scene) return;
        scene.getMeshByName(name)?.dispose();
        requestRender();
    }

    // This view only; the server and the other viewers keep theirs.
    function clear() {
        if (!scene) return;
        for (let i = scene.meshes.length - 1; i >= 0; i--) {
            const mesh = scene.meshes[i];
            if (mesh.name !== 'skyBox') mesh.dispose();
        }
        cameraPositionSet = false;
        requestRender();
    }

    function edgeLines(name, points, c) {
        const lines = BABYLON.MeshBuilder.CreateLines(name, { points: points }, scene);
        lines.color = c;
        lines.enableEdgesRendering();
        lines.edgesWidth = 5.0;
        lines.edgesColor = new BABYLON.Color4(c.r, c.g, c.b, 1);
        return lines;
    }

    function disposeTerrain() {
        if (!scene) return;
        for (let i = scene.meshes.length - 1; i >= 0; i--) {
            if (scene.meshes[i].name.startsWith('terrain_')) scene.meshes[i].dispose();
        }
    }

    // --- hub handlers (no-ops while no scene is open) --------------------------

    const handlers = {
        log: (message) => window.pathingUi?.log(message),

        removeMesh: removeMesh,

        clear: clear,

        drawLine(array, height, color, name) {
            if (!scene) return;
            removeMesh(name);
            const v = BABYLON.Vector3.FromArray(array);
            const lift = height / 10;
            edgeLines(name, [toScene(v.x, v.y, v.z), toScene(v.x, v.y, v.z, lift)], colour(color));
            if (name === 'start') setCamera(v, v, 10);
            requestRender();
        },

        drawSphere(array, color, name) {
            if (!scene) return;
            removeMesh(name);
            const v = BABYLON.Vector3.FromArray(array);
            const sphere = BABYLON.MeshBuilder.CreateSphere(name, { segments: 10, diameter: 0.5 }, scene);
            const material = new BABYLON.StandardMaterial(name + '_mat', scene);
            material.diffuseColor = colour(color);
            sphere.material = material;
            sphere.position = toScene(v.x, v.y, v.z, markerHeight(color));
            requestRender();
        },

        drawPath(arrays, color, name) {
            if (!scene || arrays.length === 0) return;

            // A small lift so a path projected onto the navmesh does not z-fight with it.
            const lift = 0.3 / div;
            const points = arrays.map(a => toScene(a[0], a[1], a[2], lift));

            removeMesh(name);
            edgeLines(name, points, colour(color));

            const first = arrays[0];
            const last = arrays[arrays.length - 1];
            setCamera({ x: first[0], y: first[1], z: first[2] }, { x: last[0], y: last[1], z: last[2] }, 20);
            requestRender();
        },

        addNavmeshTile(name, flatPositions, groundIndices, liquidIndices) {
            if (!scene) return;
            removeMesh(name + '_g');
            removeMesh(name + '_w');
            if (flatPositions.length === 0) return;

            const positions = scenePositions(flatPositions);

            const buildLayer = (suffix, indices, r, g, b, alpha) => {
                if (indices.length === 0) return;

                const mesh = new BABYLON.Mesh(name + suffix, scene);
                const normals = new Float32Array(positions.length);
                BABYLON.VertexData.ComputeNormals(positions, indices, normals);

                const data = new BABYLON.VertexData();
                data.positions = positions;
                data.indices = indices;
                data.normals = normals;
                data.applyToMesh(mesh);

                const mat = new BABYLON.StandardMaterial(name + suffix + '_mat', scene);
                mat.emissiveColor = new BABYLON.Color3(r, g, b);
                mat.disableLighting = true;
                mat.alpha = alpha;
                mat.backFaceCulling = false;
                // Pushed back in depth so a path projected onto this surface stays visible.
                mat.zOffset = 4;
                mesh.material = mat;
                mesh.parent = navmeshRoot;
            };

            buildLayer('_g', groundIndices, 0.15, 0.9, 0.3, 0.35);
            buildLayer('_w', liquidIndices, 0.15, 0.4, 0.95, 0.4);
            requestRender();
        },

        // The game geometry under one navmesh tile, by kind; only shown while Terrain is on.
        addTerrainTile(name, flatPositions, terrain, liquid, objects, models) {
            if (!scene || !terrainOn || flatPositions.length === 0) return;

            const positions = scenePositions(flatPositions);
            const uvs = new Float32Array((positions.length / 3) * 2);
            for (let i = 0, j = 0; i < positions.length; i += 3, j += 2) {
                uvs[j] = positions[i] / 4;
                uvs[j + 1] = positions[i + 2] / 4;
            }

            const kinds = [terrain, liquid, objects, models];
            for (let kind = 0; kind < TERRAIN_KINDS; kind++) {
                const meshName = name + '_' + kind;
                removeMesh(meshName);
                const indices = kinds[kind];
                if (indices.length === 0) continue;

                const mesh = new BABYLON.Mesh(meshName, scene);
                const normals = new Float32Array(positions.length);
                BABYLON.VertexData.ComputeNormals(positions, indices, normals);

                const data = new BABYLON.VertexData();
                data.positions = positions;
                data.indices = indices;
                data.normals = normals;
                data.uvs = uvs;
                data.applyToMesh(mesh);

                mesh.material = terrainMaterials[kind];
                mesh.parent = terrainRoots[kind];
            }
            requestRender();
        }
    };

    function connect() {
        if (connection || typeof signalR === 'undefined') return;

        connection = new signalR.HubConnectionBuilder()
            .withUrl('/watchHub')
            .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
            .withAutomaticReconnect([0, 3000, 5000, 10000, 15000, 30000])
            .build();

        for (const [name, handler] of Object.entries(handlers)) {
            connection.on(name, handler);
        }

        // What the other viewers already see: the lanes' tiles and the last routes.
        connection.onreconnected(() => replay());
        connection.start()
            .then(() => replay())
            .catch(err => window.pathingUi?.log('The watch hub is not reachable: ' + err));
    }

    function replay() {
        if (connection?.state === 'Connected') {
            connection.invoke('Replay').catch(err => console.error(err));
        }
    }

    // --- lifecycle ---------------------------------------------------------------

    function buildScene(canvas) {
        engine = new BABYLON.Engine(canvas, true);
        scene = new BABYLON.Scene(engine);
        scene.clearColor = new BABYLON.Color4(0.04, 0.06, 0.08, 1);

        const light = new BABYLON.HemisphericLight('hemi', new BABYLON.Vector3(1, 1, 0), scene);
        light.intensity = 0.9;

        camera = new BABYLON.FreeCamera('camera1', new BABYLON.Vector3(0, 50, 0), scene);
        camera.keysUp.push(87);         // w
        camera.keysDown.push(83);       // s
        camera.keysLeft.push(65);       // a
        camera.keysRight.push(68);      // d
        camera.keysDownward.push(81);   // q
        camera.keysUpward.push(69);     // e
        camera.attachControl(canvas, false);
        camera.speed = CAMERA_NORMAL;

        navmeshRoot = new BABYLON.TransformNode('navmesh', scene);

        terrainMaterials = [];
        terrainRoots = [];
        for (let i = 0; i < TERRAIN_KINDS; i++) {
            terrainRoots[i] = new BABYLON.TransformNode('terrain' + i, scene);
            const mat = new BABYLON.StandardMaterial('terrainMat' + i, scene);
            mat.diffuseTexture = new BABYLON.Texture(TEXTURE_URL + TERRAIN_TEXTURES[i], scene);
            mat.backFaceCulling = false;
            if (i === 1) mat.alpha = 0.6;
            mat.freeze();
            terrainMaterials[i] = mat;
        }

        const skybox = BABYLON.MeshBuilder.CreateBox('skyBox', { size: 4000.0 }, scene);
        const skyMaterial = new BABYLON.StandardMaterial('skyBox', scene);
        skyMaterial.backFaceCulling = false;
        skyMaterial.reflectionTexture = new BABYLON.CubeTexture(TEXTURE_URL + 'skybox', scene);
        skyMaterial.reflectionTexture.coordinatesMode = BABYLON.Texture.SKYBOX_MODE;
        skyMaterial.diffuseColor = new BABYLON.Color3(0, 0, 0);
        skyMaterial.specularColor = new BABYLON.Color3(0, 0, 0);
        skybox.material = skyMaterial;
        skyMaterial.freeze();

        // Renders only while something changed or the camera still drifts with inertia.
        const lastPos = camera.position.clone();
        const lastRot = camera.rotation.clone();
        let energy = 0;
        let shift = false;
        let alt = false;

        scene.onBeforeRenderObservable.add(() => {
            if (shift) { camera.speed = CAMERA_FAST; energy = 25; }
            else if (alt) { camera.speed = CAMERA_SLOW; energy = 25; }
            else if (energy > 0) { energy--; }
            else { camera.speed = CAMERA_NORMAL; }
        });

        engine.runRenderLoop(() => {
            if (!dirty) return;
            scene.render();

            const moved = BABYLON.Vector3.DistanceSquared(camera.position, lastPos);
            const rx = camera.rotation.x - lastRot.x;
            const ry = camera.rotation.y - lastRot.y;
            if (moved > INERTIA_EPSILON || rx * rx + ry * ry > INERTIA_EPSILON) {
                lastPos.copyFrom(camera.position);
                lastRot.copyFrom(camera.rotation);
            } else {
                dirty = false;
            }
        });

        listen(document, 'keydown', e => {
            if (isTyping(e)) return;
            dirty = true;
            if (e.key === 'Shift') shift = true;
            if (e.key === 'Alt') { alt = true; e.preventDefault(); }
        });
        listen(document, 'keyup', e => {
            if (e.key === 'Shift') shift = false;
            if (e.key === 'Alt') alt = false;
            if (isTyping(e)) return;
            dirty = true;
            if (e.key === 'o') window.pathingUi?.log('Camera position: ' + camera.position);
        });
        // The canvas follows its element, not just the window (the menu collapses).
        const resizer = new ResizeObserver(() => { engine?.resize(); requestRender(); });
        resizer.observe(canvas);
        teardown.push(() => resizer.disconnect());

        listen(canvas, 'pointermove', e => { if (e.buttons > 0) dirty = true; });
        listen(canvas, 'wheel', () => { dirty = true; }, { passive: true });

        // Size to the element now that layout has settled.
        engine.resize();
        requestRender();
    }

    async function create(canvas) {
        dispose();
        if (!window.BABYLON) {
            window.pathingUi?.log('Loading the 3D engine...');
            await window.pathingUi.loadScript(BABYLON_URL);
        }
        // The page may have been left while Babylon downloaded.
        if (!canvas.isConnected) return false;

        buildScene(canvas);
        connect();
        return true;
    }

    function dispose() {
        for (const off of teardown) off();
        teardown = [];

        // A viewer that is gone must not keep the server streaming to it.
        connection?.stop();
        connection = null;

        if (scene?.debugLayer?.isVisible()) scene.debugLayer.hide();
        engine?.stopRenderLoop();
        scene?.dispose();
        engine?.dispose();
        engine = scene = camera = navmeshRoot = null;
        terrainMaterials = [];
        terrainRoots = [];
        terrainOn = false;
        cameraPositionSet = false;
    }

    // The navmesh tiles nearest the camera, as { continent, x, z }.
    function tilesNearCamera(limit) {
        const seen = new Map();
        for (const mesh of scene.meshes) {
            const match = NAVMESH_TILE.exec(mesh.name);
            if (!match) continue;
            const key = match[1] + '_' + match[2] + '_' + match[3];
            if (seen.has(key)) continue;
            const centre = mesh.getBoundingInfo().boundingBox.centerWorld;
            seen.set(key, {
                continent: match[1], x: Number(match[2]), z: Number(match[3]),
                distance: BABYLON.Vector3.DistanceSquared(centre, camera.position)
            });
        }
        return [...seen.values()].sort((a, b) => a.distance - b.distance).slice(0, limit);
    }

    // Shows or hides the game geometry; resolves to null, or to why there is none.
    async function setTerrain(on) {
        if (!scene) return 'the 3D view is not open';
        terrainOn = on;
        if (!on) {
            disposeTerrain();
            requestRender();
            return null;
        }

        if (connection?.state !== 'Connected') return 'not connected to the watch hub';

        const near = tilesNearCamera(MAX_TERRAIN_TILES);
        if (near.length === 0) return 'no navmesh tiles in view: plan a route or press Replay first';

        // One continent per request: the nearest tile's.
        const continent = near[0].continent;
        const tiles = near.filter(t => t.continent === continent).flatMap(t => [t.x, t.z]);
        try {
            return await connection.invoke('Terrain', continent, tiles);
        } catch (err) {
            return String(err);
        }
    }

    function setNavmesh(on) {
        if (!navmeshRoot) return;
        navmeshRoot.setEnabled(on);
        requestRender();
    }

    // Terrain kind: 0 terrain, 1 liquid, 2 objects, 3 models.
    function setTerrainKind(kind, on) {
        const node = terrainRoots[kind];
        if (!node) return;
        node.setEnabled(on);
        requestRender();
    }

    function setWireframe(on) {
        if (!scene) return;
        for (const mat of terrainMaterials) {
            mat.unfreeze();
            mat.wireframe = on;
            mat.freeze();
        }
        for (const mesh of scene.meshes) {
            if (NAVMESH_TILE.test(mesh.name) && mesh.material) mesh.material.wireframe = on;
        }
        requestRender();
    }

    function setInspector(on) {
        if (!scene) return;
        if (on) scene.debugLayer.show(); else scene.debugLayer.hide();
        requestRender();
    }

    window.pathingScene = {
        create, dispose, clear, replay, setTerrain, setNavmesh, setTerrainKind, setWireframe, setInspector
    };
})();
