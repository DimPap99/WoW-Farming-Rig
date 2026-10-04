// The sidebar: zone and faction filters, the NPC, resource, utility and location categories, search, and the tools it hosts.
// Part of the Leaflet map, loaded after the pieces before it; see scripts/leaflet-pieces.mjs.

// ===== Sidebar Control =====

function getSidebarZoneId() {
    const dropdown = document.getElementById('sidebar-zone-select');
    if (dropdown && dropdown.value !== 'current') {
        return parseInt(dropdown.value);
    }
    return currentArea?.AreaID ?? null;
}

function getSidebarFactionFilter() {
    const checked = document.querySelector('input[name="sidebar-faction"]:checked');
    return checked ? checked.value : 'both';
}

function getAtlasIconStyle(iconName, iconSize = aSize) {
    const iconPos = IconAtlas[iconName];
    if (!iconPos) return '';
    const leftPx = -(iconPos[0] * iconSize);
    const topPx = -(iconPos[1] * iconSize);
    if (iconSize === aSize) {
        return `background-position: ${leftPx}px ${topPx}px;`;
    }
    const bgWidth = 16 * iconSize;
    return `background-position: ${leftPx}px ${topPx}px; background-size: ${bgWidth}px auto;`;
}

let sidebarSearchTimeout = null;
var sidebarLayersColumn = null;

function clearSidebarNpcCategories() {
    const container = document.getElementById('sidebar-npc-categories');
    if (!container) return;
    const checkboxes = container.querySelectorAll('input[type="checkbox"]');
    checkboxes.forEach(cb => { cb.checked = false; });

    // Remove associated layers
    const npcTypes = ['vendor', 'vendorammo', 'vendorfood', 'vendorpoison',
        'vendorreagent', 'repair', 'classtrainer', 'professiontrainer',
        'flightmaster', 'innkeeper', 'spirithealer', 'stablemaster'];
    for (const npcType of npcTypes) {
        activatedPOICategories.delete(`npc:${npcType}`);
        const layers = groupedOverlays[npcType];
        if (layers) {
            for (const name in layers) {
                const layer = layers[name];
                if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
                if (layer && typeof layer.destroy === 'function') layer.destroy();
                if (layerNames[name]) delete layerNames[name];
            }
            delete groupedOverlays[npcType];
        }
    }

    // Remove elites
    activatedPOICategories.delete('elites');
    const eliteLayers = groupedOverlays['Elites'];
    if (eliteLayers) {
        for (const name in eliteLayers) {
            const layer = eliteLayers[name];
            if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
            if (layer && typeof layer.destroy === 'function') layer.destroy();
            if (layerNames[name]) delete layerNames[name];
        }
        delete groupedOverlays['Elites'];
    }

    scheduleGroupedLayerControlUpdate();

    // Clear search
    const searchInput = document.getElementById('sidebar-npc-search');
    if (searchInput) searchInput.value = '';
    const searchResults = document.getElementById('sidebar-search-results');
    if (searchResults) searchResults.innerHTML = '';
}

function clearSidebarResourceCategories() {
    const resBody = document.getElementById('sidebar-resource-body');
    if (!resBody) return;
    const checkboxes = resBody.querySelectorAll('input[type="checkbox"]');
    checkboxes.forEach(cb => { cb.checked = false; });

    const toRemove = ['skinnable', 'node:vein', 'node:herb', 'mailbox'];
    for (const cat of toRemove) {
        activatedPOICategories.delete(cat);
    }

    // Remove layers by group name patterns
    const groupPatterns = ['Skinning', 'vein', 'herb', 'Mailboxes'];
    for (const gName in groupedOverlays) {
        const shouldRemove = groupPatterns.some(p => gName.includes(p));
        if (!shouldRemove) continue;
        const layers = groupedOverlays[gName];
        for (const name in layers) {
            const layer = layers[name];
            if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
            if (layer && typeof layer.destroy === 'function') layer.destroy();
            if (layerNames[name]) delete layerNames[name];
        }
        delete groupedOverlays[gName];
    }
    scheduleGroupedLayerControlUpdate();
}

function clearSidebarUtilityCategories() {
    const utilBody = document.getElementById('sidebar-utility-body');
    if (!utilBody) return;
    const checkboxes = utilBody.querySelectorAll('input[type="checkbox"]');
    checkboxes.forEach(cb => { cb.checked = false; });

    if (ADTEnabled) removeADT();

    // Remove POI layers
    const layers = groupedOverlays['POI'];
    if (layers) {
        for (const name in layers) {
            const layer = layers[name];
            if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
            if (layer && typeof layer.destroy === 'function') layer.destroy();
            if (layerNames[name]) delete layerNames[name];
        }
        delete groupedOverlays['POI'];
    }

    if (recordPlayerPath) setRecord(false);
    scheduleGroupedLayerControlUpdate();
}

function clearSidebarLocationCategories() {
    const locBody = document.getElementById('sidebar-location-body');
    if (!locBody) return;
    const checkboxes = locBody.querySelectorAll('input[type="checkbox"]');
    checkboxes.forEach(cb => { cb.checked = false; });

    const toRemove = ['location:zones', 'location:subzones', 'location:teleport'];
    for (const cat of toRemove) {
        activatedPOICategories.delete(cat);
    }

    const groupKeys = ['Zones', 'SubZoneTexts', 'Teleport'];
    for (const gName of groupKeys) {
        const layers = groupedOverlays[gName];
        if (!layers) continue;
        for (const name in layers) {
            const layer = layers[name];
            if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
            if (layer && typeof layer.destroy === 'function') layer.destroy();
            if (layerNames[name]) delete layerNames[name];
        }
        delete groupedOverlays[gName];
    }
    scheduleGroupedLayerControlUpdate();
}

async function handleSidebarLocationChange(checkbox, locationType) {
    if (checkbox.checked) {
        activatedPOICategories.add(`location:${locationType}`);
        if (locationType === 'zones') addZones();
        else if (locationType === 'subzones') addSubZonesTexts();
        else if (locationType === 'teleport') await addTeleportPoints();
    } else {
        activatedPOICategories.delete(`location:${locationType}`);
        const groupKey = locationType === 'zones' ? 'Zones'
            : locationType === 'subzones' ? 'SubZoneTexts'
            : 'Teleport';
        const layers = groupedOverlays[groupKey];
        if (layers) {
            for (const name in layers) {
                const layer = layers[name];
                if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
                if (layer && typeof layer.destroy === 'function') layer.destroy();
                if (layerNames[name]) delete layerNames[name];
            }
            delete groupedOverlays[groupKey];
        }
        scheduleGroupedLayerControlUpdate();
    }
}

function updateSidebarResourceState() {
    const zoneId = getSidebarZoneId();
    const resBody = document.getElementById('sidebar-resource-body');
    if (!resBody) return;
    const notice = document.getElementById('sidebar-zone-notice');
    const checkboxes = resBody.querySelectorAll('.sidebar-checkbox-row');

    if (!zoneId) {
        if (notice) notice.style.display = 'block';
        checkboxes.forEach(row => row.classList.add('disabled'));
    } else {
        if (notice) notice.style.display = 'none';
        checkboxes.forEach(row => row.classList.remove('disabled'));
    }
}

function updateSidebarCurrentZone() {
    const dropdown = document.getElementById('sidebar-zone-select');
    if (!dropdown) return;
    if (dropdown.value === 'current') {
        // Update the label to show current zone name
        const opt = dropdown.options[0];
        if (currentArea) {
            opt.textContent = `Current Zone (${currentArea.AreaName})`;
        } else {
            opt.textContent = 'Current Zone (none)';
        }
    }
    updateSidebarResourceState();
}

async function handleSidebarNpcCategoryChange(checkbox, npcType) {
    const zoneId = getSidebarZoneId();
    const factionFilter = getSidebarFactionFilter();

    if (checkbox.checked) {
        activatedPOICategories.add(`npc:${npcType}`);
        await addNpc(npcType, zoneId, factionFilter);
    } else {
        activatedPOICategories.delete(`npc:${npcType}`);
        // Remove layers for this npcType
        const layers = groupedOverlays[npcType];
        if (layers) {
            for (const name in layers) {
                const layer = layers[name];
                if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
                if (layer && typeof layer.destroy === 'function') layer.destroy();
                if (layerNames[name]) delete layerNames[name];
            }
            delete groupedOverlays[npcType];
        }
        scheduleGroupedLayerControlUpdate();
    }
}

async function handleSidebarEliteChange(checkbox) {
    const zoneId = getSidebarZoneId();
    const factionFilter = getSidebarFactionFilter();

    if (checkbox.checked) {
        activatedPOICategories.add('elites');
        await addEliteNpcsToArea(zoneId, factionFilter);
    } else {
        activatedPOICategories.delete('elites');
        const layers = groupedOverlays['Elites'];
        if (layers) {
            for (const name in layers) {
                const layer = layers[name];
                if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
                if (layer && typeof layer.destroy === 'function') layer.destroy();
                if (layerNames[name]) delete layerNames[name];
            }
            delete groupedOverlays['Elites'];
        }
        scheduleGroupedLayerControlUpdate();
    }
}

async function handleSidebarResourceChange(checkbox, resourceType) {
    const zoneId = getSidebarZoneId();
    if (!zoneId) return;

    if (checkbox.checked) {
        activatedPOICategories.add(resourceType);
        if (resourceType === 'skinnable') {
            await addSkinnableNpcsToArea(zoneId);
        } else if (resourceType === 'node:vein') {
            await addNodeSpawnsToArea(zoneId, 'vein');
        } else if (resourceType === 'node:herb') {
            await addNodeSpawnsToArea(zoneId, 'herb');
        } else if (resourceType === 'mailbox') {
            await addMailboxes();
        }
        scheduleGroupedLayerControlUpdate();
    } else {
        activatedPOICategories.delete(resourceType);
        // Find and remove the associated group layers
        const groupPatterns = {
            'skinnable': 'Skinning',
            'node:vein': 'vein',
            'node:herb': 'herb',
            'mailbox': 'Mailboxes'
        };
        const pattern = groupPatterns[resourceType];
        for (const gName in groupedOverlays) {
            if (resourceType === 'mailbox' ? gName === 'Mailboxes' : gName.includes(pattern)) {
                const layers = groupedOverlays[gName];
                for (const name in layers) {
                    const layer = layers[name];
                    if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
                    if (layer && typeof layer.destroy === 'function') layer.destroy();
                    if (layerNames[name]) delete layerNames[name];
                }
                delete groupedOverlays[gName];
            }
        }
        scheduleGroupedLayerControlUpdate();
    }
}

function handleSidebarSearch() {
    const input = document.getElementById('sidebar-npc-search');
    const resultsContainer = document.getElementById('sidebar-search-results');
    if (!input || !resultsContainer) return;

    if (sidebarSearchTimeout) clearTimeout(sidebarSearchTimeout);

    sidebarSearchTimeout = setTimeout(() => {
        const query = input.value.trim();
        const factionFilter = getSidebarFactionFilter();
        const mapWide = document.getElementById('sidebar-map-wide')?.checked ?? false;
        const zoneId = mapWide ? null : getSidebarZoneId();
        const typeFamilySelect = document.getElementById('sidebar-type-family');
        const typeFamily = typeFamilySelect?.value || null;

        const results = searchCreaturesByName(query, factionFilter, zoneId, typeFamily);
        renderSearchResults(results, resultsContainer);
    }, 300);
}

function renderSearchResults(results, container) {
    container.innerHTML = '';
    if (results.length === 0) return;

    let iconIndex = 0;

    for (const creature of results) {
        const item = document.createElement('div');
        item.className = 'npc-search-result-item';

        const level = creature.MinLevel === creature.MaxLevel
            ? `[${creature.MinLevel}]`
            : `[${creature.MinLevel}-${creature.MaxLevel}]`;

        const typeName = creatureTypeNames[creature.Type] || '';
        const familyName = creatureFamilyNames[creature.Family] || '';
        const typeInfo = familyName
            ? ` - <span class="result-subname">${typeName} &gt; ${familyName}</span>`
            : typeName
                ? ` - <span class="result-subname">${typeName}</span>`
                : '';

        const iconDiv = document.createElement('div');
        iconDiv.className = 'result-icon';
        const iconName = Circles[iconIndex++ % Circles.length];
        iconDiv.style.cssText = getAtlasIconStyle(iconName, 16);
        item.appendChild(iconDiv);

        const textSpan = document.createElement('span');
        textSpan.innerHTML = `<span class="result-level">${level}</span> ${creature.Name}${typeInfo}`;
        item.appendChild(textSpan);

        item.onclick = () => {
            addNpcSpawns(creature.Entry, 'Search', iconName);
        };
        container.appendChild(item);
    }
}

async function replayActiveSidebarCategories() {
    const zoneId = getSidebarZoneId();
    const factionFilter = getSidebarFactionFilter();

    // Remove existing NPC category layers before replaying
    const npcTypes = ['vendor', 'vendorammo', 'vendorfood', 'vendorpoison',
        'vendorreagent', 'repair', 'classtrainer', 'professiontrainer',
        'flightmaster', 'innkeeper', 'spirithealer', 'stablemaster'];
    for (const npcType of npcTypes) {
        if (!activatedPOICategories.has(`npc:${npcType}`)) continue;
        const layers = groupedOverlays[npcType];
        if (layers) {
            for (const name in layers) {
                const layer = layers[name];
                if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
                if (layer && typeof layer.destroy === 'function') layer.destroy();
                if (layerNames[name]) delete layerNames[name];
            }
            delete groupedOverlays[npcType];
        }
        await addNpc(npcType, zoneId, factionFilter);
    }

    // Remove and replay elites
    if (activatedPOICategories.has('elites')) {
        const eliteLayers = groupedOverlays['Elites'];
        if (eliteLayers) {
            for (const name in eliteLayers) {
                const layer = eliteLayers[name];
                if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
                if (layer && typeof layer.destroy === 'function') layer.destroy();
                if (layerNames[name]) delete layerNames[name];
            }
            delete groupedOverlays['Elites'];
        }
        await addEliteNpcsToArea(zoneId, factionFilter);
    }

    // Remove and replay resource categories
    for (const cat of ['skinnable', 'node:vein', 'node:herb']) {
        if (!activatedPOICategories.has(cat)) continue;
        // Remove old layers
        const groupPatterns = { 'skinnable': 'Skinning', 'node:vein': 'vein', 'node:herb': 'herb' };
        const pattern = groupPatterns[cat];
        for (const gName in groupedOverlays) {
            if (gName.includes(pattern)) {
                const layers = groupedOverlays[gName];
                for (const name in layers) {
                    const layer = layers[name];
                    if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
                    if (layer && typeof layer.destroy === 'function') layer.destroy();
                    if (layerNames[name]) delete layerNames[name];
                }
                delete groupedOverlays[gName];
            }
        }
        if (zoneId) {
            if (cat === 'skinnable') await addSkinnableNpcsToArea(zoneId);
            else if (cat === 'node:vein') await addNodeSpawnsToArea(zoneId, 'vein');
            else if (cat === 'node:herb') await addNodeSpawnsToArea(zoneId, 'herb');
        }
    }

    scheduleGroupedLayerControlUpdate();
}

function createSidebar() {
    const SidebarControl = L.Control.extend({
        options: { position: 'topright' },

        onAdd: function (map) {
            // Wrapper: collapsible filter panel (left) + always-visible right rail
            const wrapper = L.DomUtil.create('div', 'sidebar-wrapper');
            L.DomEvent.disableClickPropagation(wrapper);
            L.DomEvent.disableScrollPropagation(wrapper);

            // Left: map filters panel (collapses away)
            const sidebar = L.DomUtil.create('div', 'map-filter-sidebar', wrapper);

            // Right: vertical rail flush to the map edge - toggle on top,
            // layer controls stacked below it. Stays put when the panel collapses.
            const rail = L.DomUtil.create('div', 'sidebar-right-rail', wrapper);

            // Toggle button (expands/collapses the filter panel)
            const toggleBtn = L.DomUtil.create('button', 'sidebar-toggle-btn', rail);
            toggleBtn.innerHTML = '◀';
            toggleBtn.title = 'Toggle filters';

            // Layer controls live in the rail, under the toggle
            const layersCol = L.DomUtil.create('div', 'sidebar-layers-column', rail);
            sidebarLayersColumn = layersCol;

            // Continent selector - jumps between the current era's continents.
            // Manual navigation only; in follow mode the continent tracks the player.
            if (enableUrlEdit) {
                const continentRow = L.DomUtil.create('div', 'continent-selector', sidebar);
                const continentLabel = L.DomUtil.create('span', '', continentRow);
                continentLabel.textContent = 'Continent:';
                const continentSelect = L.DomUtil.create('select', '', continentRow);
                const continentNames = { 'Expansion01': 'Outland' };
                for (const key of eraContinents(expansion)) {
                    const opt = L.DomUtil.create('option', '', continentSelect);
                    opt.value = key;
                    opt.textContent = continentNames[key] || key;
                    if (key === continent) opt.selected = true;
                }
                continentSelect.onchange = () => {
                    const z = LeafletMap.getZoom();
                    const center = LeafletMap.getCenter();
                    window.location.href = '/Leaflet/' + expansion + '/' + continentSelect.value
                        + '/' + z + '/' + center.lat.toFixed(3) + '/' + center.lng.toFixed(3) + '/';
                };
                L.DomEvent.disableClickPropagation(continentRow);
            }

            // Content wrapper
            const content = L.DomUtil.create('div', 'sidebar-content', sidebar);

            // Header
            const header = L.DomUtil.create('div', 'sidebar-header', content);
            header.textContent = 'Map Filters';

            let isCollapsed = false;
            const collapseFilters = (collapsed) => {
                isCollapsed = collapsed;
                sidebar.style.display = isCollapsed ? 'none' : '';
                toggleBtn.innerHTML = isCollapsed ? '▶' : '◀';
            };
            toggleBtn.onclick = () => collapseFilters(!isCollapsed);

            // ===== NPCs Section =====
            this._buildNpcSection(content);

            // ===== Resources Section =====
            this._buildResourceSection(content);

            // ===== Locations Section =====
            this._buildLocationSection(content);

            // ===== Utilities Section =====
            this._buildUtilitySection(content);

            if (enableUrlEdit && typeof bakeBuildSection === 'function') {
                this._buildBakeSection(content);
            }

            // Auto-collapse on small screens or when not in edit mode
            if (window.innerWidth <= 768 || !enableUrlEdit) {
                collapseFilters(true);
            }

            return wrapper;
        },

        _buildSectionHeader: function (parent, title, clearFn) {
            const section = L.DomUtil.create('div', 'sidebar-section', parent);

            const header = L.DomUtil.create('div', 'section-header', section);
            const headerLeft = L.DomUtil.create('div', 'section-header-left', header);
            const chevron = L.DomUtil.create('span', 'section-chevron', headerLeft);
            chevron.textContent = '▼';
            const titleSpan = L.DomUtil.create('span', '', headerLeft);
            titleSpan.textContent = title;

            const clearBtn = L.DomUtil.create('button', 'section-clear-btn', header);
            clearBtn.textContent = 'Clear';
            clearBtn.onclick = (e) => {
                e.stopPropagation();
                if (clearFn) clearFn();
            };

            const body = L.DomUtil.create('div', 'section-body', section);

            header.onclick = (e) => {
                if (e.target === clearBtn) return;
                const isCollapsed = body.classList.toggle('collapsed');
                chevron.classList.toggle('collapsed', isCollapsed);
            };

            return body;
        },

        _buildNpcSection: function (parent) {
            const body = this._buildSectionHeader(parent, 'NPCs', clearSidebarNpcCategories);

            // Zone dropdown
            const zoneLabel = L.DomUtil.create('div', 'section-label', body);
            zoneLabel.textContent = 'Zone';
            const zoneSelect = L.DomUtil.create('select', 'sidebar-select', body);
            zoneSelect.id = 'sidebar-zone-select';

            const currentOpt = document.createElement('option');
            currentOpt.value = 'current';
            currentOpt.textContent = currentArea
                ? `Current Zone (${currentArea.AreaName})`
                : 'Current Zone (none)';
            zoneSelect.appendChild(currentOpt);

            // Populate with parent zones sorted by name
            const parentZones = Object.values(Zones)
                .filter(z => z.ParentAreaId === 0 && z.AreaID < AreaIDOffset)
                .sort((a, b) => a.AreaName.localeCompare(b.AreaName));

            for (const zone of parentZones) {
                const opt = document.createElement('option');
                opt.value = zone.AreaID;
                opt.textContent = zone.AreaName;
                zoneSelect.appendChild(opt);
            }

            zoneSelect.onchange = () => {
                updateSidebarResourceState();
                replayActiveSidebarCategories();
            };

            // Faction filter
            const factionLabel = L.DomUtil.create('div', 'section-label', body);
            factionLabel.textContent = 'Faction';
            const factionGroup = L.DomUtil.create('div', 'sidebar-radio-group', body);

            const factions = [
                { value: 'both', label: 'Both' },
                { value: 'alliance', label: 'Alliance' },
                { value: 'horde', label: 'Horde' }
            ];
            for (const f of factions) {
                const lbl = L.DomUtil.create('label', '', factionGroup);
                const radio = document.createElement('input');
                radio.type = 'radio';
                radio.name = 'sidebar-faction';
                radio.value = f.value;
                if (f.value === 'both') radio.checked = true;
                radio.onchange = () => replayActiveSidebarCategories();
                lbl.appendChild(radio);
                const span = document.createElement('span');
                span.textContent = f.label;
                lbl.appendChild(span);
            }

            // NPC category checkboxes
            const categoryList = L.DomUtil.create('div', '', body);
            categoryList.id = 'sidebar-npc-categories';

            const npcCategories = [
                { type: 'vendor', label: 'Vendor', icon: 'vendor' },
                { type: 'vendorammo', label: 'Vendor Ammo', icon: 'vendorammo' },
                { type: 'vendorfood', label: 'Vendor Food', icon: 'vendorfood' },
                { type: 'vendorpoison', label: 'Vendor Poison', icon: 'vendorpoison' },
                { type: 'vendorreagent', label: 'Vendor Reagent', icon: 'vendorreagent' },
                { type: 'repair', label: 'Repair', icon: 'repair' },
                { type: 'classtrainer', label: 'Class Trainer', icon: 'classtrainer' },
                { type: 'professiontrainer', label: 'Profession Trainer', icon: 'professiontrainer' },
                { type: 'flightmaster', label: 'Flight Master', icon: 'flightmaster' },
                { type: 'innkeeper', label: 'Innkeeper', icon: 'innkeeper' },
                { type: 'spirithealer', label: 'Spirit Healer', icon: 'spirithealer' },
                { type: 'stablemaster', label: 'Stable Master', icon: 'stablemaster' },
            ];

            for (const cat of npcCategories) {
                const row = L.DomUtil.create('label', 'sidebar-checkbox-row', categoryList);
                const cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.dataset.npcType = cat.type;
                cb.onchange = () => handleSidebarNpcCategoryChange(cb, cat.type);
                row.appendChild(cb);

                const iconDiv = L.DomUtil.create('div', 'cb-icon', row);
                iconDiv.style.cssText = getAtlasIconStyle(cat.icon, 20);

                const label = L.DomUtil.create('span', 'cb-label', row);
                label.textContent = cat.label;
            }

            // Elites checkbox
            const eliteRow = L.DomUtil.create('label', 'sidebar-checkbox-row', categoryList);
            const eliteCb = document.createElement('input');
            eliteCb.type = 'checkbox';
            eliteCb.dataset.npcType = 'elites';
            eliteCb.onchange = () => handleSidebarEliteChange(eliteCb);
            eliteRow.appendChild(eliteCb);

            const eliteIconDiv = L.DomUtil.create('div', 'cb-icon', eliteRow);
            eliteIconDiv.style.cssText = getAtlasIconStyle('Elite', 20);

            const eliteLabel = L.DomUtil.create('span', 'cb-label', eliteRow);
            eliteLabel.textContent = 'Elites';

            // Type / Family dropdown
            const typeFamilyLabel = L.DomUtil.create('div', 'section-label', body);
            typeFamilyLabel.textContent = 'Type / Family';
            const typeFamilySelect = L.DomUtil.create('select', 'sidebar-select', body);
            typeFamilySelect.id = 'sidebar-type-family';

            const allOpt = document.createElement('option');
            allOpt.value = '';
            allOpt.textContent = 'All';
            typeFamilySelect.appendChild(allOpt);

            const typeGroup = document.createElement('optgroup');
            typeGroup.label = 'Creature Type';
            for (const [id, name] of Object.entries(creatureTypeNames)) {
                const opt = document.createElement('option');
                opt.value = `type:${id}`;
                opt.textContent = name;
                typeGroup.appendChild(opt);
            }
            typeFamilySelect.appendChild(typeGroup);

            const familyGroup = document.createElement('optgroup');
            familyGroup.label = 'Beast Family';
            for (const [id, name] of Object.entries(creatureFamilyNames)) {
                const opt = document.createElement('option');
                opt.value = `family:${id}`;
                opt.textContent = name;
                familyGroup.appendChild(opt);
            }
            typeFamilySelect.appendChild(familyGroup);

            typeFamilySelect.onchange = handleSidebarSearch;

            // Name search
            const searchLabel = L.DomUtil.create('div', 'section-label', body);
            searchLabel.textContent = 'Search NPC';
            const searchInput = L.DomUtil.create('input', 'sidebar-input', body);
            searchInput.id = 'sidebar-npc-search';
            searchInput.type = 'text';
            searchInput.placeholder = 'Type NPC name...';
            searchInput.onkeyup = handleSidebarSearch;

            // Map-wide toggle
            const toggleRow = L.DomUtil.create('div', 'sidebar-toggle-row', body);
            const mapWideCheck = document.createElement('input');
            mapWideCheck.type = 'checkbox';
            mapWideCheck.id = 'sidebar-map-wide';
            toggleRow.appendChild(mapWideCheck);
            const toggleLabel = L.DomUtil.create('label', '', toggleRow);
            toggleLabel.textContent = 'Search map-wide';
            toggleLabel.htmlFor = 'sidebar-map-wide';

            // Search results container
            const searchResults = L.DomUtil.create('div', 'npc-search-results', body);
            searchResults.id = 'sidebar-search-results';
        },

        _buildResourceSection: function (parent) {
            const body = this._buildSectionHeader(parent, 'Resources', clearSidebarResourceCategories);
            body.id = 'sidebar-resource-body';

            const notice = L.DomUtil.create('div', 'zone-required-notice', body);
            notice.id = 'sidebar-zone-notice';
            notice.textContent = 'Select a zone to show resources';

            const resourceCategories = [
                { type: 'skinnable', label: 'Skinnable', icon: 'skinnable' },
                { type: 'node:vein', label: 'Mining Veins', icon: 'mining' },
                { type: 'node:herb', label: 'Herbs', icon: 'Silverleaf' },
                { type: 'mailbox', label: 'Mailboxes', icon: 'mailbox' },
            ];

            for (const cat of resourceCategories) {
                const row = L.DomUtil.create('label', 'sidebar-checkbox-row', body);
                const cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.dataset.resourceType = cat.type;
                cb.onchange = () => handleSidebarResourceChange(cb, cat.type);
                row.appendChild(cb);

                const iconDiv = L.DomUtil.create('div', 'cb-icon', body);
                iconDiv.style.cssText = getAtlasIconStyle(cat.icon, 20);
                row.appendChild(iconDiv);

                const label = L.DomUtil.create('span', 'cb-label', row);
                label.textContent = cat.label;
            }

            // Initial state
            requestAnimationFrame(() => updateSidebarResourceState());
        },

        _buildLocationSection: function (parent) {
            const body = this._buildSectionHeader(parent, 'Locations', clearSidebarLocationCategories);
            body.id = 'sidebar-location-body';

            const locationCategories = [
                { type: 'zones', label: 'Zones', icon: 'darkblue' },
                { type: 'subzones', label: 'Sub Zones', icon: 'green' },
                { type: 'teleport', label: 'Teleport Points', icon: 'poi' },
            ];

            for (const cat of locationCategories) {
                const row = L.DomUtil.create('label', 'sidebar-checkbox-row', body);
                const cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.dataset.locationType = cat.type;
                cb.onchange = () => handleSidebarLocationChange(cb, cat.type);
                row.appendChild(cb);

                const iconDiv = L.DomUtil.create('div', 'cb-icon', row);
                iconDiv.style.cssText = getAtlasIconStyle(cat.icon, 20);

                const label = L.DomUtil.create('span', 'cb-label', row);
                label.textContent = cat.label;
            }
        },

        _buildBakeSection: function (parent) {
            const body = this._buildSectionHeader(parent, 'Navmesh Bake', null);
            body.id = 'sidebar-bake-body';
            bakeBuildSection(body);
        },

        _buildUtilitySection: function (parent) {
            const body = this._buildSectionHeader(parent, 'Utilities', clearSidebarUtilityCategories);
            body.id = 'sidebar-utility-body';

            // ADT Grid
            const adtRow = L.DomUtil.create('label', 'sidebar-checkbox-row', body);
            const adtCb = document.createElement('input');
            adtCb.type = 'checkbox';
            adtCb.onchange = () => {
                if (adtCb.checked) addADT();
                else removeADT();
            };
            adtRow.appendChild(adtCb);
            const adtLabel = L.DomUtil.create('span', 'cb-label', adtRow);
            adtLabel.textContent = 'ADT Grid';

            // Navmesh coverage - which ground actually has a baked navmesh.
            // Guarded: these live in sibling scripts, and a stale cached copy of
            // one must not take the whole map down with it.
            if (typeof setNavmeshTilesVisible === 'function') {
            const nmTileRow = L.DomUtil.create('label', 'sidebar-checkbox-row', body);
            const nmTileCb = document.createElement('input');
            nmTileCb.type = 'checkbox';
            nmTileCb.onchange = () => setNavmeshTilesVisible(nmTileCb.checked);
            nmTileRow.appendChild(nmTileCb);
            const nmTileLabel = L.DomUtil.create('span', 'cb-label', nmTileRow);
            nmTileLabel.textContent = 'Navmesh Tiles';

            const nmAdtRow = L.DomUtil.create('label', 'sidebar-checkbox-row', body);
            const nmAdtCb = document.createElement('input');
            nmAdtCb.type = 'checkbox';
            nmAdtCb.onchange = () => setNavmeshAdtVisible(nmAdtCb.checked);
            nmAdtRow.appendChild(nmAdtCb);
            const nmAdtLabel = L.DomUtil.create('span', 'cb-label', nmAdtRow);
            nmAdtLabel.textContent = 'Navmesh ADT Coverage';
            }

            // Road authoring: a drawn polyline means a road, not a bot path.
            if (enableUrlEdit && typeof setRoadDrawMode === 'function') {
                const roadRow = L.DomUtil.create('label', 'sidebar-checkbox-row', body);
                const roadCb = document.createElement('input');
                roadCb.type = 'checkbox';
                roadCb.checked = getRoadDrawMode();
                roadCb.onchange = () => setRoadDrawMode(roadCb.checked);
                roadRow.appendChild(roadCb);
                const roadLabel = L.DomUtil.create('span', 'cb-label', roadRow);
                roadLabel.textContent = 'Draw Roads';
                roadRow.title = 'When on, a drawn polyline becomes a road instead of a bot path';
            }

            // POI Points
            const poiRow = L.DomUtil.create('label', 'sidebar-checkbox-row', body);
            const poiCb = document.createElement('input');
            poiCb.type = 'checkbox';
            poiCb.onchange = () => {
                if (poiCb.checked) addPoi(getSidebarZoneId() ?? currentArea?.AreaID);
                else {
                    const layers = groupedOverlays['POI'];
                    if (layers) {
                        for (const name in layers) {
                            const layer = layers[name];
                            if (LeafletMap.hasLayer(layer)) LeafletMap.removeLayer(layer);
                            if (layer && typeof layer.destroy === 'function') layer.destroy();
                            if (layerNames[name]) delete layerNames[name];
                        }
                        delete groupedOverlays['POI'];
                    }
                    scheduleGroupedLayerControlUpdate();
                }
            };
            poiRow.appendChild(poiCb);
            const poiIconDiv = L.DomUtil.create('div', 'cb-icon', poiRow);
            poiIconDiv.style.cssText = getAtlasIconStyle('poi', 20);
            const poiLabel = L.DomUtil.create('span', 'cb-label', poiRow);
            poiLabel.textContent = 'POI Points';

            // Record Path (only when enableUrlEdit)
            if (enableUrlEdit) {
                const recRow = L.DomUtil.create('label', 'sidebar-checkbox-row', body);
                const recCb = document.createElement('input');
                recCb.type = 'checkbox';
                recCb.onchange = () => setRecord(recCb.checked);
                recRow.appendChild(recCb);
                const recIconDiv = L.DomUtil.create('div', 'cb-icon', recRow);
                recIconDiv.style.cssText = getAtlasIconStyle('redcross', 20);
                const recLabel = L.DomUtil.create('span', 'cb-label', recRow);
                recLabel.textContent = 'Record Path';
            }

            // Coordinate Marker
            const coordLabel = L.DomUtil.create('div', 'section-label', body);
            coordLabel.textContent = 'Coordinate Marker';
            coordLabel.style.marginTop = '8px';

            const coordRow = L.DomUtil.create('div', 'sidebar-coord-row', body);
            const coordInput = L.DomUtil.create('input', 'sidebar-input', coordRow);
            coordInput.type = 'text';
            coordInput.placeholder = 'x y z [mapId]';
            coordInput.style.flex = '1';
            coordInput.style.marginBottom = '0';

            const coordBtn = L.DomUtil.create('button', 'sidebar-coord-btn', coordRow);
            coordBtn.textContent = '📍';
            coordBtn.title = 'Add marker at coordinates';

            const addCoord = () => {
                const text = coordInput.value.trim();
                if (!text) return;
                const parts = text.split(/\s+/);
                if (parts.length < 3) { alert('Format: x y z [mapId]'); return; }
                const x = parseFloat(parts[0]);
                const y = parseFloat(parts[1]);
                const z = parseFloat(parts[2]);
                const mapId = parts.length >= 4 ? parseInt(parts[3]) : null;
                if (isNaN(x) || isNaN(y) || isNaN(z)) { alert('Invalid coordinates'); return; }
                if (mapId !== null && mapId !== config.MapID) {
                    alert(`Map ${mapId} does not match current map ${config.MapID}`);
                    return;
                }
                addCustomMarker(x, y, z);
                coordInput.value = '';
            };

            coordBtn.onclick = addCoord;
            coordInput.onkeydown = (e) => { if (e.key === 'Enter') addCoord(); };
        }
    });

    return new SidebarControl();
}

