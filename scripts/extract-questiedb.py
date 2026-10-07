#!/usr/bin/env python3
"""Build WoW: Forever's world data from QuestieDB - NPCs, spawns, herbs, veins,
mailboxes and quests - into the files the rig already reads, plus the quest files.

QuestieDB (https://github.com/Questie/QuestieDB) is the database behind the Questie
add-on. Its Forever flavor is Classic Era's data moved onto Forever's maps (13,691
coordinates converted where a zone's map changed) plus Forever's new content, imported
from Wowhead's Forever pages and from positions Questie's players recorded. Its own
export merges those layers; this script runs that export, reads the result as data
(no Lua is evaluated here) and writes:

  Json/dbc/forever/creatures.json           every NPC: names, levels, rank, faction, roles
  Json/npcspawnlocations/forever/<map>.json every NPC spawn, world x/y (z 0: find the ground)
  Json/area/forever/<areaId>.json           per zone: vendors, trainers, repairers,
                                            innkeepers, flight masters, herbs, veins, skinnable
  Json/mailboxlocations/forever/<map>.json  mailboxes
  Json/quests/forever/quests.json           every quest: giver, turn-in, levels, races,
                                            classes, chain, objectives, explore areas
  Json/quests/forever/objects.json          every object with spawns, world x/y
  Json/quests/forever/items.json            the items quests need: who drops them, what
                                            holds them, who sells them

Forever is Classic plus more, so what QuestieDB does not say about an old-world NPC is
taken from Classic's own creatures.json (Json/dbc/som): creature type, family and skin
loot (skinnable = beasts and the rest Classic marks skinnable), and the vendor and
trainer kinds. Forever's new NPCs carry no role or type in QuestieDB yet.

COORDINATES: QuestieDB gives a spawn as {x, y} in 0-100 of its zone's map, the [a, b]
of Json/area (see extract-area-sqldump.py): worldY = left + a% of (right - left),
worldX = top + b% of (bottom - top), with the zone's WorldMapArea bounds. Spawns in a
zone this client has no outdoor map for (dungeons, raids) are left out and counted.

USAGE (Linux or Windows; QuestieDB bundles its own Lua 5.1 for both)
  git clone --depth 1 https://github.com/Questie/QuestieDB ../QuestieDB
  python scripts/extract-questiedb.py ../QuestieDB
  python scripts/extract-questiedb.py ../QuestieDB --no-export --dry-run
"""
import argparse
import collections
import json
import math
import os
import platform
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

CLIENT = "forever"
CLASSIC = "som"

EXPORT_SCRIPT = os.path.join("tools", "export-forever.lua")
EXPORT_DIR = os.path.join("src", "corrections", "Forever", "combined")
BUNDLED_LUA = {"Linux": os.path.join("tools", "lua-binary", "linux-x64", "lua"),
               "Windows": os.path.join("tools", "lua-binary", "lua.exe")}

# QuestieDB's NPC flags are cmangos-classic's (Questie's npcDB legend links Unit.h);
# SharedLib/Data/NpcFlags.cs is the TBC+ layout, as Utilities/ClassifyVendorFlags remaps
# Classic's own data to.
CLASSIC_TO_NPC_FLAGS = {
    0x0001: 1 << 0,   # Gossip
    0x0002: 1 << 1,   # QuestGiver
    0x0004: 1 << 7,   # Vendor
    0x0008: 1 << 13,  # FlightMaster
    0x0010: 1 << 4,   # Trainer
    0x0020: 1 << 14,  # SpiritHealer
    0x0040: 1 << 15,  # SpiritGuide
    0x0080: 1 << 16,  # Innkeeper
    0x0100: 1 << 17,  # Banker
    0x0200: 1 << 18,  # Petitioner
    0x0400: 1 << 19,  # TabardDesigner
    0x0800: 1 << 20,  # Battlemaster
    0x1000: 1 << 21,  # Auctioneer
    0x2000: 1 << 22,  # StableMaster
    0x4000: 1 << 12,  # Repair
}
NPC_TRAINER = 1 << 4
NPC_CLASS_TRAINER = 1 << 5
NPC_PROFESSION_TRAINER = 1 << 6
NPC_VENDOR = 1 << 7
NPC_VENDOR_KINDS = (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11)
NPC_REPAIR = 1 << 12
NPC_FLIGHTMASTER = 1 << 13
NPC_INNKEEPER = 1 << 16

CLASSES = ("Warrior", "Paladin", "Hunter", "Rogue", "Priest", "Shaman", "Mage", "Warlock", "Druid")

# Forever's races (QuestieDB's src/corrections/enum/expansions.lua): Classic's bits plus
# Skyborne at bits 32 and 33, which a 32-bit mask would lose.
ALLIANCE = 1 | 4 | 8 | 64 | (1 << 32)
HORDE = 2 | 16 | 32 | 128 | (1 << 33)

CREATURE_TYPE_BEAST = 1

# Field positions, 1-based as the export's legends name them.
NPC = dict(name=1, minLevel=4, maxLevel=5, rank=6, spawns=7, zoneID=9, questStarts=10, questEnds=11,
           factionID=12, friendlyToFaction=13, subName=14, npcFlags=15)
OBJECT = dict(name=1, questStarts=2, questEnds=3, spawns=4, zoneID=5)
ITEM = dict(name=1, npcDrops=2, objectDrops=3, itemDrops=4, startQuest=5, vendors=14)
QUEST = dict(name=1, startedBy=2, finishedBy=3, requiredLevel=4, questLevel=5, requiredRaces=6,
             requiredClasses=7, objectivesText=8, triggerEnd=9, objectives=10, sourceItemId=11,
             preQuestGroup=12, preQuestSingle=13, exclusiveTo=16, zoneOrSort=17, requiredSkill=18,
             requiredSourceItems=21, nextQuestInChain=22, specialFlags=24, breadcrumbForQuestId=27,
             extraObjectives=29, requiredMaxLevel=32)

QUEST_REPEATABLE = 1


# --- Lua table literals, read as data ---------------------------------------------------

_TOKEN = re.compile(r'''\s*(?:(--[^\n]*)|("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')|'''
                    r'''(-?0x[0-9a-fA-F]+|-?\d+\.?\d*(?:[eE][-+]?\d+)?|-?\.\d+)|([A-Za-z_]\w*)|(\S))''', re.S)
_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "\\": "\\", '"': '"', "'": "'", "\n": "\n",
            "a": "\a", "b": "\b", "f": "\f", "v": "\v"}


def _lua_string(raw):
    body, out, i = raw[1:-1], [], 0
    while i < len(body):
        c = body[i]
        if c != "\\":
            out.append(c)
            i += 1
            continue
        n = body[i + 1]
        if n.isdigit():
            j = i + 1
            while j < len(body) and j < i + 4 and body[j].isdigit():
                j += 1
            out.append(chr(int(body[i + 1:j])))
            i = j
            continue
        out.append(_ESCAPES.get(n, n))
        i += 2
    return "".join(out)


def _tokens(text):
    for m in _TOKEN.finditer(text):
        comment, string, number, name, sym = m.groups()
        if comment is not None:
            continue
        if string is not None:
            yield "str", _lua_string(string)
        elif number is not None:
            if "x" in number.lower():
                yield "num", int(number, 16)
            elif any(c in number for c in ".eE"):
                yield "num", float(number)
            else:
                yield "num", int(number)
        elif name is not None:
            yield "name", name
        elif sym is not None:
            yield "sym", sym


class _Reader:
    """A Lua table constructor of constants: nil, booleans, numbers, strings, tables.
    A table with only positional values is a list (nil holes kept as None); one with keys
    is a dict, its positional values under 1, 2, ... as Lua numbers them."""

    def __init__(self, text):
        self.toks = list(_tokens(text))
        self.i = 0

    def take(self):
        t = self.toks[self.i]
        self.i += 1
        return t

    def peek(self, k=0):
        return self.toks[self.i + k] if self.i + k < len(self.toks) else (None, None)

    def value(self):
        kind, v = self.take()
        if kind in ("str", "num"):
            return v
        if kind == "name" and v in ("nil", "true", "false"):
            return {"nil": None, "true": True, "false": False}[v]
        if kind == "sym" and v == "{":
            return self.table()
        raise ValueError(f"unexpected {kind} {v!r}")

    def table(self):
        listed, keyed = [], {}
        while True:
            kind, v = self.peek()
            if (kind, v) == ("sym", "}"):
                self.take()
                break
            if (kind, v) == ("sym", "["):
                self.take()
                key = self.value()
                self.take()  # ]
                self.take()  # =
                keyed[key] = self.value()
            elif kind == "name" and self.peek(1) == ("sym", "="):
                self.take()
                self.take()
                keyed[v] = self.value()
            else:
                listed.append(self.value())
            if self.peek() in (("sym", ","), ("sym", ";")):
                self.take()
        if not keyed:
            return listed
        for index, item in enumerate(listed, 1):
            keyed[index] = item
        return keyed


_ROW = re.compile(r"^(\w+)\[(\d+)\]=(\{.*\})\s*$")


def read_export(path):
    """id -> row (1-based list, None for nil) from one of the export's files."""
    rows = {}
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            m = _ROW.match(line)
            if not m:
                continue
            row = _Reader(m.group(3)).value()
            if isinstance(row, dict):
                width = max((k for k in row if isinstance(k, int)), default=0)
                row = [row.get(i) for i in range(1, width + 1)]
            rows[int(m.group(2))] = row
    return rows


def field(row, index):
    return row[index - 1] if len(row) >= index else None


def positional(table, index):
    """A tuple field of a row ({creatures, objects, items}), lists or dicts alike."""
    if isinstance(table, dict):
        return table.get(index)
    if isinstance(table, list) and len(table) >= index:
        return table[index - 1]
    return None


def ids(values):
    return sorted({int(v) for v in values or [] if isinstance(v, (int, float))})


# --- maps ---------------------------------------------------------------------------------

def load_zones(client):
    path = os.path.join(ROOT, "Json", "dbc", client, "WorldMapArea.json")
    if not os.path.isfile(path):
        sys.exit(f"missing {path} - run ReadDBC_CSV -v {client} worldmap")
    zones = {}
    for r in json.load(open(path, encoding="utf-8-sig")):
        if r.get("UIMapId") and r["AreaID"] not in zones:
            zones[r["AreaID"]] = r
    return zones


def to_world(zone, a, b):
    """[a, b] in 0-100 of the zone's map -> world (x, y); the inverse of to_map."""
    x = zone["LocTop"] + (zone["LocBottom"] - zone["LocTop"]) * b / 100.0
    y = zone["LocLeft"] + (zone["LocRight"] - zone["LocLeft"]) * a / 100.0
    return round(x, 2), round(y, 2)


def spawn_points(spawns):
    """{zoneId: [[x, y], ...]} as QuestieDB keeps them -> [(zoneId, a, b), ...]."""
    out = []
    if not isinstance(spawns, dict):
        return out
    for zone_id, points in spawns.items():
        for p in points or []:
            if isinstance(p, list) and len(p) >= 2 and p[0] is not None and p[1] is not None and p[0] >= 0 and p[1] >= 0:
                out.append((int(zone_id), float(p[0]), float(p[1])))
    return out


# --- main ---------------------------------------------------------------------------------

def run_export(questiedb, lua):
    if not lua:
        bundled = BUNDLED_LUA.get(platform.system())
        if bundled is None:
            sys.exit(f"no bundled Lua for {platform.system()}: pass --lua with a Lua 5.1 interpreter")
        lua = os.path.join(questiedb, bundled)
        if platform.system() != "Windows" and os.path.isfile(lua) and not os.access(lua, os.X_OK):
            os.chmod(lua, 0o755)
    print(f"  QuestieDB export: {lua} {EXPORT_SCRIPT} --include-authored")
    done = subprocess.run([lua, EXPORT_SCRIPT, "--include-authored"], cwd=questiedb, capture_output=True, text=True)
    if done.returncode != 0:
        sys.exit(f"QuestieDB's export failed:\n{done.stdout}{done.stderr}")
    for line in done.stdout.splitlines():
        print("    " + line)


def questiedb_version(questiedb):
    try:
        done = subprocess.run(["git", "log", "-1", "--format=%h %cs"], cwd=questiedb, capture_output=True, text=True)
        return done.stdout.strip() or "unknown"
    except OSError:
        return "unknown"


def node_names():
    """Herb and vein names from every client's area files, so the list grows with the data."""
    herbs, veins = set(), set()
    area_root = os.path.join(ROOT, "Json", "area")
    for client in os.listdir(area_root):
        folder = os.path.join(area_root, client)
        if client == CLIENT or not os.path.isdir(folder):
            continue
        for name in os.listdir(folder):
            if not name.endswith(".json"):
                continue
            try:
                a = json.load(open(os.path.join(folder, name), encoding="utf-8-sig"))
            except (OSError, ValueError):
                continue
            herbs.update((a.get("herb") or {}).keys())
            veins.update((a.get("vein") or {}).keys())
    return herbs, veins


def node_kind(name, herbs, veins):
    if name in herbs:
        return "herb"
    if name in veins or name.endswith(" Vein") or name.endswith(" Deposit"):
        return "vein"
    return None


def classic_trainer_kind(sub_name):
    if not sub_name:
        return 0
    if any(sub_name.startswith(c + " Trainer") for c in CLASSES):
        return NPC_CLASS_TRAINER
    if sub_name.endswith("Trainer"):
        return NPC_PROFESSION_TRAINER
    return 0


def npc_flags(classic_flags, sub_name, classic):
    flags = 0
    for bit, mapped in CLASSIC_TO_NPC_FLAGS.items():
        if classic_flags & bit:
            flags |= mapped
    # The kinds QuestieDB does not keep come from Classic's curated flags where the NPC is
    # the same, else from the title ("Warrior Trainer", "Mining Trainer").
    if classic is not None:
        if flags & NPC_VENDOR:
            flags |= classic.get("NpcFlag", 0) & NPC_VENDOR_KINDS
        if flags & NPC_TRAINER:
            flags |= classic.get("NpcFlag", 0) & (NPC_CLASS_TRAINER | NPC_PROFESSION_TRAINER)
    if flags & NPC_TRAINER and not flags & (NPC_CLASS_TRAINER | NPC_PROFESSION_TRAINER):
        flags |= classic_trainer_kind(sub_name)
    return flags


def side(races):
    if not races:
        return "Both"
    alliance, horde = races & ALLIANCE, races & HORDE
    if alliance and not horde:
        return "Alliance"
    if horde and not alliance:
        return "Horde"
    return "Both"


def write_json(path, value, one_per_line=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        if one_per_line and isinstance(value, list):
            fh.write("[\n")
            fh.write(",\n".join(json.dumps(v, separators=(",", ":"), ensure_ascii=False) for v in value))
            fh.write("\n]\n")
        else:
            json.dump(value, fh, separators=(",", ":"), ensure_ascii=False)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("questiedb", help="a QuestieDB checkout")
    ap.add_argument("--lua", help="a Lua 5.1 interpreter instead of QuestieDB's bundled one")
    ap.add_argument("--no-export", action="store_true", help="read the export already in the checkout")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    if not os.path.isfile(os.path.join(args.questiedb, EXPORT_SCRIPT)):
        sys.exit(f"{args.questiedb} is not a QuestieDB checkout with {EXPORT_SCRIPT}")
    if not args.no_export:
        run_export(args.questiedb, args.lua)
    version = questiedb_version(args.questiedb)

    exported = os.path.join(args.questiedb, EXPORT_DIR)
    npcs = read_export(os.path.join(exported, "foreverNpcDB.lua"))
    objects = read_export(os.path.join(exported, "foreverObjectDB.lua"))
    items = read_export(os.path.join(exported, "foreverItemDB.lua"))
    quests = read_export(os.path.join(exported, "foreverQuestDB.lua"))
    print(f"  QuestieDB {version}: {len(quests)} quests, {len(npcs)} NPCs, {len(objects)} objects, {len(items)} items")

    zones = load_zones(CLIENT)
    classic = {c["Entry"]: c for c in json.load(open(os.path.join(ROOT, "Json", "dbc", CLASSIC, "creatures.json"),
                                                      encoding="utf-8-sig"))}
    herbs, veins = node_names()
    dropped = collections.Counter()

    def world_points(spawns, kind):
        out = []
        for zone_id, a, b in spawn_points(spawns):
            zone = zones.get(zone_id)
            if zone is None:
                dropped[kind] += 1
                continue
            x, y = to_world(zone, a, b)
            out.append((zone["MapID"], x, y, zone_id, a, b))
        return out

    # creatures.json, and the spawns by map ------------------------------------------
    creatures = []
    npc_spawns = collections.defaultdict(dict)
    npc_flags_of = {}
    skinnable = set()
    for entry, row in sorted(npcs.items()):
        name = field(row, NPC["name"]) or ""
        sub_name = field(row, NPC["subName"])
        known = classic.get(entry)
        flags = npc_flags(field(row, NPC["npcFlags"]) or 0, sub_name, known)
        npc_flags_of[entry] = flags
        creature = {
            "Entry": entry,
            "Name": name,
            "SubName": sub_name,
            "MinLevel": field(row, NPC["minLevel"]) or 0,
            "MaxLevel": field(row, NPC["maxLevel"]) or 0,
            "Rank": field(row, NPC["rank"]) or 0,
            "Faction": field(row, NPC["factionID"]) or 0,
            "NpcFlag": flags,
            "SkinLoot": known.get("SkinLoot", 0) if known else 0,
            "Family": known.get("Family", 0) if known else 0,
            "Type": known.get("Type", 0) if known else 0,
        }
        creatures.append(creature)
        if creature["SkinLoot"] or creature["Type"] == CREATURE_TYPE_BEAST:
            skinnable.add(entry)
        for map_id, x, y, _, _, _ in world_points(field(row, NPC["spawns"]), "npc spawns"):
            npc_spawns[map_id].setdefault(str(entry), []).append({"x": x, "y": y, "z": 0})

    # area/<areaId>.json -------------------------------------------------------------------
    areas = {}

    def area_of(zone_id):
        return areas.setdefault(zone_id, {
            "flightmaster": [], "innkeeper": [], "repair": [], "vendor": [], "trainer": [],
            "herb": {}, "vein": {}, "skinnable": [], "gatherable": [], "minable": [], "salvegable": [],
        })

    area_skinnable = collections.defaultdict(set)
    for entry, row in sorted(npcs.items()):
        flags = npc_flags_of[entry]
        roles = [role for role, bit in (("flightmaster", NPC_FLIGHTMASTER), ("innkeeper", NPC_INNKEEPER),
                                        ("repair", NPC_REPAIR), ("vendor", NPC_VENDOR), ("trainer", NPC_TRAINER))
                 if flags & bit]
        friendly = field(row, NPC["friendlyToFaction"]) or ""
        by_zone = collections.defaultdict(list)
        for zone_id, a, b in spawn_points(field(row, NPC["spawns"])):
            if zone_id in zones:
                by_zone[zone_id].append([round(a, 2), round(b, 2)])
        for zone_id, coords in by_zone.items():
            if entry in skinnable:
                area_skinnable[zone_id].add(entry)
            if not roles:
                continue
            npc = {
                "coords": coords,
                "level": field(row, NPC["maxLevel"]) or 0,
                "name": field(row, NPC["name"]) or "",
                "type": 0,
                "id": entry,
                # Wowhead's convention, which the map reads: 1 friendly, -1 hostile.
                "reacthorde": 1 if "H" in friendly else -1,
                "reactalliance": 1 if "A" in friendly else -1,
                "description": field(row, NPC["subName"]) or "",
            }
            for role in roles:
                area_of(zone_id)[role].append(npc)
    for zone_id, entries in area_skinnable.items():
        area_of(zone_id)["skinnable"] = sorted(entries)

    # Objects: nodes, mailboxes, and objects.json -------------------------------------------
    mailboxes = collections.defaultdict(list)
    object_out = []
    unclassified = collections.Counter()
    for entry, row in sorted(objects.items()):
        name = field(row, OBJECT["name"]) or ""
        points = world_points(field(row, OBJECT["spawns"]), "object spawns")
        if not points:
            continue
        kind = node_kind(name, herbs, veins)
        for map_id, x, y, zone_id, a, b in points:
            if kind is not None:
                bucket = area_of(zone_id)[kind].setdefault(name, [{"coords": []}])
                bucket[0]["coords"].append([round(a, 2), round(b, 2)])
            if name == "Mailbox":
                mailboxes[map_id].append({"x": x, "y": y, "z": 0})
        if kind is None and name != "Mailbox" and len(points) >= 25:
            unclassified[name] += len(points)
        object_out.append({
            "id": entry,
            "name": name,
            "spawns": [{"map": m, "x": x, "y": y} for m, x, y, _, _, _ in points],
        })

    # quests.json ---------------------------------------------------------------------------
    quest_out = []
    wanted_items = set()
    for entry, row in sorted(quests.items()):
        started = field(row, QUEST["startedBy"])
        finished = field(row, QUEST["finishedBy"])
        objectives = field(row, QUEST["objectives"])
        goals = []
        for kind, index in (("kill", 1), ("object", 2), ("item", 3)):
            for goal in positional(objectives, index) or []:
                if isinstance(goal, list) and goal and goal[0] is not None:
                    goals.append({"kind": kind, "id": int(goal[0]), "text": goal[1] if len(goal) > 1 else None})
                    if kind == "item":
                        wanted_items.add(int(goal[0]))
        reputation = positional(objectives, 4)
        if isinstance(reputation, list) and len(reputation) >= 2 and reputation[0]:
            goals.append({"kind": "reputation", "id": int(reputation[0]), "value": reputation[1]})
        for credit in positional(objectives, 5) or []:
            if isinstance(credit, list) and len(credit) >= 2:
                goals.append({"kind": "kill", "id": int(credit[1]) if credit[1] else 0, "ids": ids(credit[0]),
                              "text": credit[2] if len(credit) > 2 else None})
        for spell in positional(objectives, 6) or []:
            if isinstance(spell, list) and spell and spell[0] is not None:
                goals.append({"kind": "spell", "id": int(spell[0]), "text": spell[1] if len(spell) > 1 else None,
                              "item": spell[2] if len(spell) > 2 else None})

        explore = []
        trigger = field(row, QUEST["triggerEnd"])
        if isinstance(trigger, list) and len(trigger) >= 2:
            places = world_points(trigger[1], "explore points")
            explore.append({"text": trigger[0], "places": [{"map": m, "x": x, "y": y} for m, x, y, _, _, _ in places]})
        for extra in field(row, QUEST["extraObjectives"]) or []:
            if isinstance(extra, list) and extra and isinstance(extra[0], dict):
                places = world_points(extra[0], "extra objective points")
                explore.append({"text": extra[2] if len(extra) > 2 else None,
                                "places": [{"map": m, "x": x, "y": y} for m, x, y, _, _, _ in places]})

        source_items = ids([field(row, QUEST["sourceItemId"])] + list(field(row, QUEST["requiredSourceItems"]) or []))
        start_items = ids(positional(started, 3))
        wanted_items.update(source_items)
        wanted_items.update(start_items)
        skill = field(row, QUEST["requiredSkill"])
        races = field(row, QUEST["requiredRaces"]) or 0
        quest_out.append({
            "id": entry,
            "name": field(row, QUEST["name"]) or "",
            "level": field(row, QUEST["questLevel"]) or 0,
            "minLevel": field(row, QUEST["requiredLevel"]) or 0,
            "maxLevel": field(row, QUEST["requiredMaxLevel"]) or 0,
            "races": races,
            "side": side(races),
            "classes": field(row, QUEST["requiredClasses"]) or 0,
            "zone": field(row, QUEST["zoneOrSort"]) or 0,
            "starts": {"npcs": ids(positional(started, 1)), "objects": ids(positional(started, 2)), "items": start_items},
            "ends": {"npcs": ids(positional(finished, 1)), "objects": ids(positional(finished, 2))},
            "objectives": goals,
            "explore": explore,
            "text": [t for t in field(row, QUEST["objectivesText"]) or [] if isinstance(t, str)],
            "preAll": ids(field(row, QUEST["preQuestGroup"])),
            "preAny": ids(field(row, QUEST["preQuestSingle"])),
            "exclusive": ids(field(row, QUEST["exclusiveTo"])),
            "next": field(row, QUEST["nextQuestInChain"]) or 0,
            "breadcrumbFor": field(row, QUEST["breadcrumbForQuestId"]) or 0,
            "repeatable": bool((field(row, QUEST["specialFlags"]) or 0) & QUEST_REPEATABLE),
            "skill": {"id": skill[0], "value": skill[1]} if isinstance(skill, list) and len(skill) >= 2 else None,
            "sourceItems": source_items,
        })

    # items.json: what the quests need --------------------------------------------------------
    item_out = []
    for entry in sorted(wanted_items):
        row = items.get(entry)
        if row is None:
            dropped["quest items unknown to QuestieDB"] += 1
            continue
        item_out.append({
            "id": entry,
            "name": field(row, ITEM["name"]) or "",
            "npcDrops": ids(field(row, ITEM["npcDrops"])),
            "objectDrops": ids(field(row, ITEM["objectDrops"])),
            "itemDrops": ids(field(row, ITEM["itemDrops"])),
            "vendors": ids(field(row, ITEM["vendors"])),
            "startQuest": field(row, ITEM["startQuest"]) or 0,
        })

    # Report ---------------------------------------------------------------------------------
    roles = collections.Counter()
    for a in areas.values():
        for role in ("vendor", "repair", "trainer", "innkeeper", "flightmaster"):
            roles[role] += len(a[role])
    print(f"  zones with data {len(areas)}; " + ", ".join(f"{k} {v}" for k, v in roles.items()))
    print(f"  herb points {sum(len(n[0]['coords']) for a in areas.values() for n in a['herb'].values())}, "
          f"vein points {sum(len(n[0]['coords']) for a in areas.values() for n in a['vein'].values())}, "
          f"mailboxes {sum(len(v) for v in mailboxes.values())}, skinnable NPCs {len(skinnable)}")
    print(f"  NPC spawns {sum(len(p) for m in npc_spawns.values() for p in m.values())} on maps {sorted(npc_spawns)}")
    print(f"  quests {len(quest_out)} ({sum(1 for q in quest_out if q['objectives'] or q['explore'])} with objectives), "
          f"objects {len(object_out)}, quest items {len(item_out)}")
    print("  left out: " + (", ".join(f"{k} {v}" for k, v in sorted(dropped.items())) or "nothing"))
    if unclassified:
        print("  objects with 25+ spawns that are neither herb nor vein (check for new node names):")
        for name, count in unclassified.most_common(12):
            print(f"    {count:5d}  {name}")

    # Classic's world positions of the same NPCs: how well the conversion lands.
    distances = []
    for map_id, spawns in npc_spawns.items():
        path = os.path.join(ROOT, "Json", "npcspawnlocations", CLASSIC, f"{map_id}.json")
        if not os.path.isfile(path):
            continue
        known = json.load(open(path, encoding="utf-8-sig"))
        for entry, points in spawns.items():
            theirs = known.get(entry)
            if theirs and len(points) == 1 and len(theirs) == 1:
                distances.append(math.dist((points[0]["x"], points[0]["y"]), (theirs[0]["x"], theirs[0]["y"])))
    if distances:
        distances.sort()
        near = sum(1 for d in distances if d <= 5) / len(distances)
        print(f"  check against Classic's own spawns: {len(distances)} NPCs, median {distances[len(distances) // 2]:.1f} yd, "
              f"{near:.0%} within 5 yd")

    if args.dry_run:
        print("  (dry-run) nothing written")
        return

    dbc = os.path.join(ROOT, "Json", "dbc", CLIENT)
    with open(os.path.join(dbc, "creatures.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(creatures, fh, indent=2, ensure_ascii=False)

    for folder in ("npcspawnlocations", "mailboxlocations", "area"):
        target = os.path.join(ROOT, "Json", folder, CLIENT)
        if os.path.isdir(target):
            for name in os.listdir(target):
                if name.endswith(".json"):
                    os.remove(os.path.join(target, name))
    for map_id, spawns in npc_spawns.items():
        write_json(os.path.join(ROOT, "Json", "npcspawnlocations", CLIENT, f"{map_id}.json"), spawns)
    for map_id, points in mailboxes.items():
        write_json(os.path.join(ROOT, "Json", "mailboxlocations", CLIENT, f"{map_id}.json"), points)
    for zone_id, area in sorted(areas.items()):
        write_json(os.path.join(ROOT, "Json", "area", CLIENT, f"{zone_id}.json"), area)

    quest_dir = os.path.join(ROOT, "Json", "quests", CLIENT)
    write_json(os.path.join(quest_dir, "quests.json"), quest_out, one_per_line=True)
    write_json(os.path.join(quest_dir, "objects.json"), object_out, one_per_line=True)
    write_json(os.path.join(quest_dir, "items.json"), item_out, one_per_line=True)
    write_json(os.path.join(quest_dir, "source.json"), {"questiedb": version, "client": CLIENT})
    print(f"  wrote creatures.json, {len(npc_spawns)} spawn maps, {len(mailboxes)} mailbox maps, "
          f"{len(areas)} zone files and the quest files")


if __name__ == "__main__":
    main()
