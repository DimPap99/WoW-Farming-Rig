// The Leaflet map's script comes in pieces: Frontend/wwwroot/script/leaflet-watch.js
// and the leaflet-*.js files listed below, loaded in this order by both hosts.
// The pieces are contiguous runs of one script in its own order, so joined they
// are that script again. `join` writes the whole script (to stdout or a file),
// `split` cuts a whole script back into the pieces, and `check` confirms that the
// pieces split where the table says and join without loss. Moving code across a
// piece boundary means updating `starts` below; run `check` afterwards.
// Node only, no packages.

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');
const folder = join(repo, 'Frontend', 'wwwroot', 'script');

// Each piece starts at the first line, after the previous piece's start, equal
// to `starts`, or `back` lines before it. The first piece starts at the top and
// carries no header, so leaflet-watch.js keeps the script's opening lines.
const pieces = [
    { file: 'leaflet-watch.js' },
    {
        file: 'leaflet-map.js', starts: 'async function getDBC(database) {',
        about: 'The map\'s data and lifecycle: the DBC lookups and NPC search, popups, creating and disposing the map, and area changes.',
    },
    {
        file: 'leaflet-layers.js', starts: 'function angleDifference(a, b) {',
        about: 'The player marker and path recording, live paths, the icon atlas and sprite layers, and the grouped layer control.',
    },
    {
        file: 'leaflet-coords.js', starts: 'function synchronizeTitleAndURL() {',
        about: 'Clicks and coordinates: the page URL, sprite and map clicks, world/map/screen conversions, saving drawn paths, the zone at a point, and the ADT grid.',
    },
    {
        file: 'leaflet-spawns.js', starts: '// Coordinate Marker Control moved to sidebar',
        about: 'Points of interest: custom markers, teleports, NPC and node spawns, skinnables, mailboxes, NPCs by type and elites.',
    },
    {
        file: 'leaflet-sidebar.js', starts: '// ===== Sidebar Control =====',
        about: 'The sidebar: zone and faction filters, the NPC, resource, utility and location categories, search, and the tools it hosts.',
    },
    {
        file: 'leaflet-paths.js', starts: 'function setImage(style, npcType) {',
        about: 'Zone outlines and labels, route files drawn on the map, and the draw and edit tools with their right-click menu.',
    },
    {
        file: 'leaflet-signalr.js', starts: '////////////// signalR ///////////////////////////', back: 1,
        about: 'PathingAPI\'s live view over its SignalR watch hub; nothing happens where signalR is not loaded.',
    },
];

const BOM = String.fromCharCode(0xFEFF);
const WHOLE = 'leaflet-watch.js';

const fromDisk = file => readFileSync(join(folder, file), 'utf8');

function header(piece, eol) {
    return [
        `// ${piece.about}`,
        `// Part of the Leaflet map, loaded after the pieces before it; see scripts/leaflet-pieces.mjs.`,
        '',
    ].join(eol) + eol;
}

// The whole script as lines, with its byte order mark and line ending.
function parse(text) {
    const bom = text.startsWith(BOM);
    if (bom) text = text.slice(1);
    const eol = text.includes('\r\n') ? '\r\n' : '\n';
    return { lines: text.split(eol), bom, eol };
}

function split(whole) {
    const { lines, bom, eol } = whole;
    const starts = [0];
    for (const piece of pieces.slice(1)) {
        const from = starts[starts.length - 1] + 1;
        const at = lines.indexOf(piece.starts, from);
        if (at < 0) throw new Error(`${piece.file}: '${piece.starts}' not found after line ${from}`);
        starts.push(at - (piece.back ?? 0));
    }
    return pieces.map((piece, i) => {
        const body = lines.slice(starts[i], i + 1 < starts.length ? starts[i + 1] : lines.length).join(eol);
        // A piece but the last ends with a line break, as its lines did in the whole.
        const text = i + 1 < pieces.length ? body + eol : body;
        return { file: piece.file, text: i === 0 ? (bom ? BOM : '') + text : header(piece, eol) + text };
    });
}

function joinPieces() {
    return pieces.map((piece, i) => {
        const text = fromDisk(piece.file);
        if (i === 0) return text;
        const eol = text.includes('\r\n') ? '\r\n' : '\n';
        const expected = header(piece, eol);
        if (!text.startsWith(expected)) throw new Error(`${piece.file}: the header is not the one this script writes`);
        return text.slice(expected.length);
    }).join('');
}

function write(whole) {
    for (const piece of split(parse(whole)))
        writeFileSync(join(folder, piece.file), piece.text, 'utf8');
}

const [command, argument] = process.argv.slice(2);
switch (command) {
    case 'split': {
        write(readFileSync(argument ?? join(folder, WHOLE), 'utf8'));
        break;
    }
    case 'join': {
        const text = joinPieces();
        if (argument) writeFileSync(argument, text, 'utf8'); else process.stdout.write(text);
        break;
    }
    case 'check': {
        const joined = joinPieces();
        let ok = true;
        for (const piece of split(parse(joined))) {
            if (fromDisk(piece.file) !== piece.text) {
                console.error(`${piece.file} is not where the table splits the joined script`);
                ok = false;
            }
        }
        console.log(ok ? `ok: ${pieces.length} pieces, ${joined.split('\n').length} lines joined` : 'pieces out of step');
        process.exit(ok ? 0 : 1);
    }
    default:
        console.error('usage: node scripts/leaflet-pieces.mjs split [whole.js] | join [whole.js] | check');
        process.exit(2);
}
