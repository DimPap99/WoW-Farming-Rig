// 512
// Tiles are stored and served per mesh ERA (see DataConfig.ClientEra /
// NavmeshSettings.MeshEra), so identical old-world art is not duplicated per
// expansion. som/tbc/wrath all resolve to 'precata'.
const Configs = {
    'precata': {
        'Azeroth': {
            resX: 10752,
            resY: 21504,
            maxZoom: 6,
            MapID: 0,
            offset: {
                min: { x: 20, y: 24 },
            }
        },
        'Kalimdor': {
            resX: 26112,
            resY: 28672,
            maxZoom: 6,
            MapID: 1,
            offset: {
                min: { x: 0, y: 0 },
            }
        },
        'Expansion01': {
            resX: 25088,
            resY: 19968,
            maxZoom: 6,
            MapID: 530,
            offset: {
                min: { x: 6, y: 12 },
            }
        },
        'Northrend': {
            // Filled from scripts/extract-minimap.py manifest output.
            resX: 19968,
            resY: 14848,
            maxZoom: 6,
            MapID: 571,
            offset: {
                min: { x: 9, y: 11 },
            }
        }
    },
    // Cataclysm re-terraformed the old world and added zones, so the cata era
    // has its own art with a different extent/offset than precata - Azeroth is
    // 839 minimap blocks here against precata's 687.
    // Generate with: LEAFLET_ERA=cata WOW_MPQ=<cata client>\Data
    //                python scripts/extract-minimap.py
    'cata': {
        'Azeroth': {
            resX: 14848,
            resY: 21504,
            maxZoom: 6,
            MapID: 0,
            offset: {
                min: { x: 20, y: 17 },
            }
        },
        'Kalimdor': {
            resX: 29184,
            resY: 28672,
            maxZoom: 6,
            MapID: 1,
            offset: {
                min: { x: 0, y: 0 },
            }
        },
        // Standalone maps Cataclysm added. They are not zones of Azeroth/Kalimdor -
        // each has its own MapID - which is why they were missing until now.
        'Deephome': {           // Deepholm
            resX: 5120,
            resY: 5120,
            maxZoom: 6,
            MapID: 646,
            offset: {
                min: { x: 25, y: 26 },
            }
        },
        'LostIsles': {          // The Lost Isles + Kezan (goblin start)
            resX: 5120,
            resY: 14336,
            maxZoom: 6,
            MapID: 648,
            offset: {
                min: { x: 24, y: 23 },
            }
        },
        'Gilneas2': {           // Gilneas (worgen start)
            resX: 4608,
            resY: 3584,
            maxZoom: 6,
            MapID: 654,
            offset: {
                min: { x: 31, y: 24 },
            }
        },
        'MaelstromZone': {      // The Maelstrom
            resX: 2560,
            resY: 2560,
            maxZoom: 6,
            MapID: 730,
            offset: {
                min: { x: 28, y: 28 },
            }
        }
        // Expansion01 and Northrend are intentionally absent: their art is
        // inherited from precata via TILE_SHARED_CONTINENTS below.
    },
    // Mists has its own era (see clientEra). HawaiiMainLand is Pandaria, which
    // exists only here - no earlier era has art for it.
    // Generated with:
    //   LEAFLET_ERA=mop WOW_MPQ=<mop client>\Data python scripts/extract-minimap.py
    'mop': {
        'Azeroth': {
            resX: 14848,
            resY: 21504,
            maxZoom: 6,
            MapID: 0,
            offset: {
                min: { x: 20, y: 17 },
            }
        },
        'Kalimdor': {
            resX: 29184,
            resY: 28672,
            maxZoom: 6,
            MapID: 1,
            offset: {
                min: { x: 0, y: 0 },
            }
        },
        'Expansion01': {
            resX: 25088,
            resY: 19968,
            maxZoom: 6,
            MapID: 530,
            offset: {
                min: { x: 6, y: 12 },
            }
        },
        'Northrend': {
            resX: 19968,
            resY: 14848,
            maxZoom: 6,
            MapID: 571,
            offset: {
                min: { x: 9, y: 11 },
            }
        },
        'HawaiiMainLand': {
            resX: 15360,
            resY: 12288,
            maxZoom: 6,
            MapID: 870,
            offset: {
                min: { x: 18, y: 16 },
            }
        },
        // Carried over from Cataclysm - Mists ships the same art for these, so the
        // values match the cata block above.
        'Deephome': {           // Deepholm
            resX: 5120,
            resY: 5120,
            maxZoom: 6,
            MapID: 646,
            offset: {
                min: { x: 25, y: 26 },
            }
        },
        'LostIsles': {          // The Lost Isles + Kezan (goblin start)
            resX: 5120,
            resY: 14336,
            maxZoom: 6,
            MapID: 648,
            offset: {
                min: { x: 24, y: 23 },
            }
        },
        'Gilneas2': {           // Gilneas (worgen start)
            resX: 4608,
            resY: 3584,
            maxZoom: 6,
            MapID: 654,
            offset: {
                min: { x: 31, y: 24 },
            }
        },
        'MaelstromZone': {      // The Maelstrom
            resX: 2560,
            resY: 2560,
            maxZoom: 6,
            MapID: 730,
            offset: {
                min: { x: 28, y: 28 },
            }
        },
        'NewRaceStartZone': {   // The Wandering Isle (pandaren start) - Mists only
            resX: 5120,
            resY: 5120,
            maxZoom: 6,
            MapID: 860,
            offset: {
                min: { x: 26, y: 20 },
            }
        }
    },
};

// Mirrors DataConfig.ClientEra: which tile/config era a client uses.
function clientEra(expansion) {
    switch ((expansion || '').toLowerCase()) {
        case 'vanilla': case 'classic': case 'som':
        case 'tbc': case 'bcc': case 'wrath': case 'wotlk':
        case 'legacy_vanilla': case 'legacy_tbc': case 'legacy_wrath':
            return 'precata';
        case 'cata': case 'legacy_cata':
            return 'cata';
        // Mists is its own era, not Cataclysm's: the two worlds are close but
        // not equal, and Mists adds Pandaria, which Cataclysm has no art for.
        case 'mop': case 'legacy_mop':
            return 'mop';
        default:
            return (expansion || '').toLowerCase();
    }
}

// Mirrors DataConfig.TileSharedContinents / TileEra: Cataclysm rebuilt the old
// world but left Northrend and Outland essentially alone, so those two keep
// reading the precata tiles instead of duplicating ~1950 minimap blocks. Tiles
// only - the navmesh is always baked per era, because the near-identical art
// hides real geometry drift (see the C# doc comment).
const TILE_SHARED_CONTINENTS = ['Northrend', 'Expansion01'];

function tileEra(expansion, continent) {
    const era = clientEra(expansion);
    return era === 'cata' && TILE_SHARED_CONTINENTS.includes(continent) ? 'precata' : era;
}

// The continent's tile config, following the shared-art fallback above.
function continentConfig(expansion, continent) {
    const cfgs = Configs[tileEra(expansion, continent)];
    return cfgs ? cfgs[continent] : undefined;
}

// Every continent this client can show, including the ones it inherits.
function eraContinents(expansion) {
    const names = Object.keys(Configs[clientEra(expansion)] || {});
    for (const c of TILE_SHARED_CONTINENTS) {
        if (!names.includes(c) && continentConfig(expansion, c)) {
            names.push(c);
        }
    }
    return names;
}

const aSize = 32;

const AreaIDOffset = 1000000;

var areaCache = {};
var WMADB = {};
var Zones = {};
var SubZones = {};
var AreaTableById = {};   // areaId -> { AreaID, MapID, ParentAreaId, AreaName }
var creatures = {};
var spawnLocations = {};
let factionTemplates = {};

const clickableSprites = new Set();

let savedLayerVisibility = null;
const activatedPOICategories = new Set();

const skinnableExclude = {
    21: true,       // Ram
    721: true,      // Rabiit
    883: true,      // Deer
    890: true,      // Fawn
    1933: true,     // Sheep
    2442: true,     // Cow
    2620: true,     // Prairie dog  
    5951: true,     // Hare
    7846: true,     // Teremus the Devourer
    8759: true,     // Mosshoof Runner
    12298: true,    // Sickly Deer
};

const raceNames = {
    'Human': true,
    'Dwarf': true,
    'NightElf': true,
    'Gnome': true,
    'Orc': true,
    'Troll': true,
    'Tauren': true,
    'Undead': true,
    'Draenei': true,
    'BloodElf': true,
    'Goblin': true,
    'Worgen': true,
    'Pandaren': true
}

const creatureTypeNames = {
    1: 'Beast', 2: 'Dragonkin', 3: 'Demon', 4: 'Elemental',
    5: 'Giant', 6: 'Undead', 7: 'Humanoid', 8: 'Critter',
    9: 'Mechanical', 11: 'Totem'
};

const creatureFamilyNames = {
    1: 'Wolf', 2: 'Cat', 3: 'Spider', 4: 'Bear', 5: 'Boar',
    6: 'Crocolisk', 7: 'Carrion Bird', 8: 'Crab', 9: 'Gorilla',
    11: 'Raptor', 12: 'Tallstrider', 15: 'Felhunter', 16: 'Voidwalker',
    17: 'Succubus', 20: 'Scorpid', 21: 'Turtle', 24: 'Bat',
    25: 'Hyena', 26: 'Bird of Prey', 27: 'Wind Serpent'
};

const raceToZone = {
    'Human': 12,
    'Dwarf': 1,
    'NightElf': 141,
    'Gnome': 1,
    'Orc': 14,
    'Troll': 14,
    'Tauren': 215,
    'Undead': 85,

    // todo fix below
    'Draenei': 21,
    'BloodElf': 22,

    'Goblin': 23,
    'Worgen': 24,
    'Pandaren': 25
}

var maxZoom = 10;

var expansion = "som";
var continent = 'Azeroth';
var startZoom = 4;

var baseUrl = "https://www.wowhead.com/classic";

// The Pathing Service's API as the host forwards it: the Control Center proxies it, and
// BlazorServer forwards /api/v1 to the service it is configured with. The routes are
// Contracts.Pathing.PathingApi's and the static data prefixes Contracts.Pathing.WorldData's;
// change one, change both.
const PATHING_API = '/api/v1';
const TELEPORT_LOCATIONS_URL = '/locations/teleport_locations.txt';

let pathingOfflineReported = false;

// fetch() against the Pathing Service that never throws: { ok, status, body, error }. A service
// that is down answers 502 or 504 through the proxy, or nothing at all; that is said once, on the
// map, until the service answers again, and the map keeps showing tiles and spawns without it. A
// write is not sent to a service that serves another era's data (pathingOtherEra).
async function pathingFetch(path, init) {
    if (init && init.method && init.method !== 'GET') {
        const other = await pathingOtherEra();
        if (other) {
            mapNotice(other);
            return { ok: false, status: 409, body: null, error: other };
        }
    }

    let response;
    try {
        response = await fetch(PATHING_API + path, init);
    } catch (err) {
        reportPathingOffline(String(err));
        return { ok: false, status: 0, body: null, error: 'the Pathing Service could not be reached' };
    }

    let body = null;
    if ((response.headers.get('content-type') || '').includes('json')) {
        try { body = await response.json(); } catch { body = null; }
    }

    // Refusals come as problem details; detail is the sentence meant for a person.
    const error = response.ok ? null : (body && (body.detail || body.title)) || `${response.status} ${response.statusText}`;

    if (response.status === 502 || response.status === 504) {
        reportPathingOffline(error);
    } else if (pathingOfflineReported) {
        pathingOfflineReported = false;
        mapNoticeClear(OFFLINE_NOTICE);
    }

    return { ok: response.ok, status: response.status, body: body, error: error };
}

function pathingJson(method, body) {
    return { method: method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) };
}

const OFFLINE_NOTICE = 'offline';

// What the service serves (/capabilities): read at most every PATHING_SERVES_MS, as it may be
// restarted with another --exp while the map is open.
const PATHING_SERVES_MS = 30000;
let pathingServes = null;
let pathingServesAt = 0;

// Why the map must not write to the service, or null: the service serves another era's data (a
// Classic bot's map beside a service started with --exp=forever), and what the map saved would go
// to that client's folders, its "Clear cache" delete that client's tiles. Unknown is not refused:
// a service that does not answer refuses the write itself.
async function pathingOtherEra() {
    if (!pathingServes || Date.now() - pathingServesAt > PATHING_SERVES_MS) {
        try {
            const response = await fetch(PATHING_API + '/capabilities');
            pathingServes = response.ok ? await response.json() : null;
            pathingServesAt = Date.now();
        } catch {
            pathingServes = null;
        }
    }

    const mine = clientEra(expansion);
    if (!pathingServes || !pathingServes.era || pathingServes.era === mine) {
        return null;
    }

    return `Not saved: this map is ${expansion} (${mine}), but the Pathing Service serves ${pathingServes.client} `
        + `(${pathingServes.era}), and would save into that client's data. Start the service with --exp=${expansion}.`;
}

function reportPathingOffline(why) {
    if (pathingOfflineReported) return;
    pathingOfflineReported = true;
    mapNotice(`Pathing Service offline (${why}): recorded paths, cost zones, navmesh coverage, bakes and heights are unavailable`,
        'warn', OFFLINE_NOTICE);
}

// A message for the person at the map: refusals, failed saves and "offline" must be seen, not left
// in the console. It goes on the map's own notice list (bottom left), and stays until closed; the
// same message again is not listed twice. A notice given a key replaces the one before it with that
// key, and mapNoticeClear(key) takes it away. Before the map exists, the console only.
const MAX_NOTICES = 5;
let noticeControl = null;
let noticeMap = null;

function mapNotice(message, kind = 'error', key = null) {
    console.warn(message);
    const list = noticeList(true);
    if (!list) return;

    if (key) mapNoticeClear(key);
    for (const existing of list.children) {
        if (existing.dataset.message === message) {
            list.prepend(existing);
            return;
        }
    }

    const item = document.createElement('div');
    item.className = `map-notice map-notice--${kind}`;
    item.setAttribute('role', kind === 'error' ? 'alert' : 'status');
    item.dataset.message = message;
    if (key) item.dataset.key = key;

    const text = document.createElement('span');
    text.textContent = message;
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'map-notice-close';
    close.title = 'Dismiss';
    close.setAttribute('aria-label', 'Dismiss');
    close.textContent = '\u00d7';
    close.addEventListener('click', () => item.remove());

    item.append(text, close);
    list.prepend(item);
    while (list.children.length > MAX_NOTICES) {
        list.lastElementChild.remove();
    }
}

function mapNoticeClear(key) {
    const list = noticeList(false);
    if (!list) return;
    for (const item of [...list.children]) {
        if (item.dataset.key === key) item.remove();
    }
}

// The notice list of the map now shown, made on first use; null while there is no map.
function noticeList(create) {
    if (!LeafletMap) return null;
    if (noticeMap !== LeafletMap) {
        if (!create) return null;
        const NoticeControl = L.Control.extend({
            options: { position: 'bottomleft' },
            onAdd: function () {
                const box = L.DomUtil.create('div', 'map-notices');
                L.DomEvent.disableClickPropagation(box);
                L.DomEvent.disableScrollPropagation(box);
                return box;
            }
        });
        noticeControl = new NoticeControl();
        noticeControl.addTo(LeafletMap);
        noticeMap = LeafletMap;
    }
    return noticeControl.getContainer();
}

// A recorded path's library name in a URL: each segment escaped, the '/' between them kept.
function pathNameUrl(name) {
    return name.split('/').map(encodeURIComponent).join('/');
}

// Tiles are served locally when present (downloaded / generated via
// scripts/extract-minimap.py -> the /tiles route), else from the Cloudflare R2
// CDN. Decided once per continent by probing a known low-zoom tile, so a remote
// user does not 404 on every tile. `leafletTilesLocal=0` in localStorage forces
// the CDN even when local tiles exist.
const R2_TILE_ROOT = 'https://bot.tortoiseclothing.org';

// The CDN is laid out per era, the same as the local /tiles route, so the base
// has to follow the era this continent's tiles actually come from - which for
// Northrend/Outland on a cata client is still precata.
function r2TileBase(c) {
    return R2_TILE_ROOT + '/' + tileEra(expansion, c);
}

async function resolveTileBase(c) {
    if (localStorage.getItem('leafletTilesLocal') === '0') {
        return r2TileBase(c);
    }

    // If any of these local tiles is present the full set was downloaded. The
    // candidates cover offset-cropped continents (e.g. Outland has no z2x0y0).
    for (const t of ['z2x0y0', 'z2x0y1', 'z2x1y0', 'z2x1y1', 'z3x0y0']) {
        try {
            const r = await fetch('tiles/' + c + '/' + t + '.webp', { method: 'HEAD', cache: 'no-store' });
            if (r.ok) {
                return 'tiles';
            }
        } catch { /* fall through to CDN */ }
    }

    return r2TileBase(c);
}

var config;

var enableUrlEdit = false;

var maxSize;
var mapSize;
var adtSize;
var blpSize = 512;
var tileSize = 256;

var ADTEnabled = false;
const ADTGridLayer = new L.LayerGroup();
const ADTGridTextLayer = new L.LayerGroup();

const editableLayers = new L.FeatureGroup();

var playerLayer;

var recordPlayerPath = false;
var currentRecordPlayerPath = '';

var layerNames = {};

var LeafletMap;
var pixiOverlay;
var pixiContainer;
var currentResizeObserver;

var currentArea;
var lastRenderArea;

var editablePathLayerControl;

// DotNetObjectReference of the hosting Blazor page, when it supplies one.
// Used to mirror custom POIs into the search component. See leaflet-poi.js.
var dotNetHelper = null;

// Drawn search paths: thick enough to read over the map art.
const PATH_WEIGHT = 4;

var groupedLayerControls = [];
var groupedOverlays = {
    "Zones": {},
    "Paths": {},
};

let redrawScheduled = false;

function schedulePixiRedraw() {
    if (redrawScheduled || !pixiOverlay) return;

    redrawScheduled = true;
    requestAnimationFrame(() => {
        pixiOverlay.redraw();
        redrawScheduled = false;
    });
}

