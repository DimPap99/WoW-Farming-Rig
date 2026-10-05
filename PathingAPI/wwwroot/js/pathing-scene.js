// The 3D route view (PathingAPI only), driven by the watch hub.
//
// Babylon.js is ~8.6 MB, so it is fetched the first time a 3D page opens rather than
// on every page (the map and the API pages never need it). Each page visit creates
// one engine on its canvas and disposes it on leave: the old script built a new
// engine, render loop and set of document listeners per visit and never released
// them. Everything lives on window.pathingScene - no loose globals - because the
// map scripts own the global removeMesh/clear and this file used to overwrite them.

(function () {
    'use strict';

    const BABYLON_URL = 'https://cdn.babylonjs.com/babylon.js';
    const TEXTURE_URL = 'https://www.babylonjs-playground.com/textures/';
    const TEXTURES = ['grass.png', 'waterbump.png', 'floor.png', 'ground.jpg'];
    const LAYERS = 4;          // TriangleType: Terrain, Water, Object, Model
    const div = 10.0;          // world yards per scene unit
    const CAMERA_SLOW = 0.01;
    const CAMERA_NORMAL = 0.1;
    const CAMERA_FAST = 1;
    const INERTIA_EPSILON = 0.0001;

    let engine = null;
    let scene = null;
    let camera = null;
    let materials = [];
    let rootNodes = [];
    let modelId = 0;
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

    function colour(code) {
        switch (code) {
            case 1: return BABYLON.Color3.Red();
            case 2: return BABYLON.Color3.Green();
            case 3: return BABYLON.Color3.Blue();
            case 4: return BABYLON.Color3.Teal();
            case 5: return BABYLON.Color3.Teal();
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

    // HSL to Color4 along green(120) -> red(0); s=1, l=0.5, no string allocation.
    function hueToColor4(h) {
        const k1 = (0 + h / 30) % 12;
        const k2 = (8 + h / 30) % 12;
        const k3 = (4 + h / 30) % 12;
        const r = 0.5 - 0.5 * Math.max(Math.min(k1 - 3, 9 - k1, 1), -1);
        const g = 0.5 - 0.5 * Math.max(Math.min(k2 - 3, 9 - k2, 1), -1);
        const b = 0.5 - 0.5 * Math.max(Math.min(k3 - 3, 9 - k3, 1), -1);
        return new BABYLON.Color4(r, g, b, 1);
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

        drawLines(arrays, color, name) {
            if (!scene || arrays.length === 0) return;
            const lift = name.includes('debug') ? markerHeight(color) : 2.1 / div;
            removeMesh(name);

            const c = colour(color);
            const pcs = new BABYLON.PointsCloudSystem(name, div, scene);
            pcs.addPoints(arrays.length, (particle, i) => {
                const v = arrays[i];
                particle.position = toScene(v[0], v[1], v[2], lift);
                particle.color = c;
            });
            pcs.buildMeshAsync().then(requestRender);
        },

        drawWeightedLines(arrays, name) {
            if (!scene || arrays.length === 0) return;
            const lift = markerHeight(1);
            removeMesh(name);

            let minW = Infinity;
            let maxW = -Infinity;
            for (const v of arrays) {
                if (v[3] < minW) minW = v[3];
                if (v[3] > maxW) maxW = v[3];
            }
            const range = maxW - minW;

            const pcs = new BABYLON.PointsCloudSystem(name, div, scene);
            pcs.addPoints(arrays.length, (particle, i) => {
                const v = arrays[i];
                particle.position = toScene(v[0], v[1], v[2], lift);
                const t = range > 0 ? (v[3] - minW) / range : 0;
                particle.color = hueToColor4((1 - t) * 120);
            });
            pcs.buildMeshAsync().then(requestRender);
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

        addModels(loadedIndices, loadedPositions) {
            if (!scene || loadedPositions.length === 0) return;

            const start = BABYLON.Vector3.FromArray(loadedPositions[0]);
            const end = BABYLON.Vector3.FromArray(loadedPositions[loadedPositions.length - 1]);
            setCamera(start, end, 20);

            const positions = new Float32Array(loadedPositions.length * 3);
            const uvs = new Float32Array(loadedPositions.length * 2);
            const baseX = -4, baseZ = -4, scale = 4;

            for (let i = 0; i < loadedPositions.length; i++) {
                const p = loadedPositions[i];
                const index = i * 3;
                positions[index] = p[0] / div;
                positions[index + 1] = p[2] / div;
                positions[index + 2] = p[1] / div;

                uvs[i * 2] = (positions[index] - baseX) / scale;
                uvs[i * 2 + 1] = (positions[index + 2] - baseZ) / scale;
            }

            for (let layer = 0; layer < loadedIndices.length; layer++) {
                const indices = loadedIndices[layer];
                if (indices.length === 0) continue;

                modelId++;
                const mesh = new BABYLON.Mesh('custom' + modelId, scene);
                const normals = new Float32Array(positions.length);
                BABYLON.VertexData.ComputeNormals(positions, indices, normals);

                const data = new BABYLON.VertexData();
                data.positions = positions;
                data.indices = indices;
                data.normals = normals;
                data.uvs = uvs;
                data.applyToMesh(mesh);

                mesh.material = materials[layer % materials.length];
                mesh.parent = rootNodes[layer % rootNodes.length];
            }
            requestRender();
        },

        addNavmeshTile(name, flatPositions, groundIndices, liquidIndices) {
            if (!scene) return;
            removeMesh(name + '_g');
            removeMesh(name + '_w');
            if (flatPositions.length === 0) return;

            const count = flatPositions.length / 3;
            const positions = new Float32Array(flatPositions.length);
            for (let i = 0; i < count; i++) {
                const index = i * 3;
                positions[index] = flatPositions[index] / div;
                positions[index + 1] = flatPositions[index + 2] / div;
                positions[index + 2] = flatPositions[index + 1] / div;
            }

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
            };

            buildLayer('_g', groundIndices, 0.15, 0.9, 0.3, 0.35);
            buildLayer('_w', liquidIndices, 0.15, 0.4, 0.95, 0.4);
            requestRender();
        },

        drawBoundBox(min, max, color, name) {
            if (!scene) return;
            removeMesh(name);

            const a = toScene(min[0], min[1], min[2]);
            const b = toScene(max[0], max[1], max[2]);
            const vertices = [
                a.x, a.y, a.z, b.x, a.y, a.z, b.x, b.y, a.z, a.x, b.y, a.z,
                a.x, a.y, b.z, b.x, a.y, b.z, b.x, b.y, b.z, a.x, b.y, b.z
            ];
            const indices = [
                0, 1, 2, 0, 2, 3, 1, 5, 6, 1, 6, 2, 5, 4, 7, 5, 7, 6,
                4, 0, 3, 4, 3, 7, 3, 2, 6, 3, 6, 7, 4, 5, 1, 4, 1, 0
            ];

            const material = new BABYLON.StandardMaterial(name + '_mat', scene);
            material.diffuseColor = colour(color);
            material.wireframe = true;

            const box = new BABYLON.Mesh(name, scene);
            box.material = material;
            const data = new BABYLON.VertexData();
            data.positions = vertices;
            data.indices = indices;
            data.applyToMesh(box);
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

        connection.start().catch(err => console.error(err.toString()));
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

        materials = [];
        rootNodes = [];
        for (let i = 0; i < LAYERS; i++) {
            rootNodes[i] = new BABYLON.TransformNode('layer' + i, scene);
            const mat = new BABYLON.StandardMaterial('mat' + i, scene);
            mat.diffuseTexture = new BABYLON.Texture(TEXTURE_URL + TEXTURES[i], scene);
            mat.backFaceCulling = false;
            mat.freeze();
            materials[i] = mat;
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

        if (scene?.debugLayer?.isVisible()) scene.debugLayer.hide();
        engine?.stopRenderLoop();
        scene?.dispose();
        engine?.dispose();
        engine = scene = camera = null;
        materials = [];
        rootNodes = [];
        cameraPositionSet = false;
    }

    // TriangleType flag (1, 2, 4, 8) -> layer index.
    function toggleLayer(flag) {
        if (!scene) return;
        const index = Math.log2(flag);
        const node = rootNodes[index];
        if (!node) return;
        node.setEnabled(!node.isEnabled());
        requestRender();
    }

    function setWireframe(on) {
        if (!scene) return;
        for (const mat of materials) {
            mat.unfreeze();
            mat.wireframe = on;
            mat.freeze();
        }
        requestRender();
    }

    function setInspector(on) {
        if (!scene) return;
        if (on) scene.debugLayer.show(); else scene.debugLayer.hide();
        requestRender();
    }

    window.pathingScene = { create, dispose, clear, toggleLayer, setWireframe, setInspector };
})();
