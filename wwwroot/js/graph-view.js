// Globally exposed graph-view module backed by vis-network.
window.graphView = (function () {
    let network = null;
    let containerId = null;
    let dotnetRef = null;
    let lastDoc = null;
    let renderCompleteSent = false;

    async function init(id, dnref) {
        containerId = id;
        dotnetRef = dnref;
    }

    function notifyRenderComplete() {
        if (renderCompleteSent) return;
        renderCompleteSent = true;
        if (dotnetRef) {
            dotnetRef.invokeMethodAsync('OnRenderComplete').catch(() => { });
        }
    }

    // Per-node-type accent colors used in the tooltip header. Keep in sync
    // with the `groups` option passed to vis.Network below.
    const TYPE_COLORS = {
        Task:       '#4e79a7',
        TaskDetail: '#f28e2b',
        Comment:    '#e15759',
        User:       '#76b7b2',
        Requester:  '#b07aa1',
        Category:   '#59a14f'
    };

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function humanizeKey(k) {
        // detailType -> Detail Type, insertedUtc -> Inserted Utc
        const spaced = String(k).replace(/([a-z0-9])([A-Z])/g, '$1 $2');
        return spaced.charAt(0).toUpperCase() + spaced.slice(1);
    }

    function formatValue(key, val) {
        if (val === null || val === undefined || val === '') return null;
        // ISO date detection
        if (typeof val === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(val)) {
            const d = new Date(val);
            if (!isNaN(d.getTime())) {
                return d.toLocaleString();
            }
        }
        if (typeof val === 'object') {
            try { return JSON.stringify(val); } catch { return String(val); }
        }
        return String(val);
    }

    function buildTooltip(node) {
        const el = document.createElement('div');
        el.className = 'graph-tooltip';
        const accent = TYPE_COLORS[node.type] || '#555';
        el.style.setProperty('--accent', accent);

        const header = document.createElement('div');
        header.className = 'graph-tooltip-header';
        header.innerHTML =
            '<span class="graph-tooltip-type">' + escapeHtml(node.type || 'Node') + '</span>' +
            '<span class="graph-tooltip-label">' + escapeHtml(node.label || '') + '</span>';
        el.appendChild(header);

        const data = node.data || {};
        const rows = [];
        for (const k of Object.keys(data)) {
            const formatted = formatValue(k, data[k]);
            if (formatted === null) continue;
            rows.push({ key: k, value: formatted });
        }

        if (rows.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'graph-tooltip-empty';
            empty.textContent = 'No additional properties';
            el.appendChild(empty);
            return el;
        }

        const table = document.createElement('table');
        table.className = 'graph-tooltip-table';
        for (const r of rows) {
            const tr = document.createElement('tr');
            const isLong = r.value.length > 80;
            tr.innerHTML =
                '<th>' + escapeHtml(humanizeKey(r.key)) + '</th>' +
                '<td' + (isLong ? ' class="graph-tooltip-long"' : '') + '>' +
                escapeHtml(r.value) + '</td>';
            table.appendChild(tr);
        }
        el.appendChild(table);
        return el;
    }

    async function render() {
        renderCompleteSent = false;
        const container = document.getElementById(containerId);
        if (!container) { notifyRenderComplete(); return; }

        let res;
        try {
            res = await fetch('/api/graph.json');
        } catch (e) {
            container.innerHTML =
                '<div class="p-3 text-muted">Failed to load graph.json: ' + e + '</div>';
            lastDoc = null;
            notifyRenderComplete();
            return;
        }

        if (!res.ok) {
            container.innerHTML =
                '<div class="p-3 text-muted">graph.json is not built yet. Click "Rebuild graph".</div>';
            lastDoc = null;
            notifyRenderComplete();
            return;
        }
        const g = await res.json();
        lastDoc = g;

        const nodes = new vis.DataSet(g.nodes.map(n => ({
            id: n.id,
            label: n.label,
            group: n.type,
            title: buildTooltip(n),
            hidden: false
        })));
        const edges = new vis.DataSet(g.edges.map(e => ({
            id: e.id,
            from: e.source,
            to: e.target,
            label: e.type,
            arrows: 'to',
            hidden: false
        })));

        if (network) {
            network.destroy();
            network = null;
        }
        network = new vis.Network(
            container,
            { nodes, edges },
            {
                // forceAtlas2Based handles hub-and-spoke layouts (e.g., a single
                // requester linked to many tasks) much more cleanly than the
                // default Barnes-Hut solver, which tends to squash hubs together.
                // centralGravity is high enough that nodes settle quickly after
                // a drag instead of drifting forever, while damping keeps the
                // motion gentle.
                physics: {
                    stabilization: { enabled: true, iterations: 200, fit: true },
                    solver: 'forceAtlas2Based',
                    forceAtlas2Based: {
                        gravitationalConstant: -80,
                        centralGravity: 0.02,
                        springLength: 150,
                        springConstant: 0.08,
                        avoidOverlap: 0.6,
                        damping: 0.9
                    },
                    minVelocity: 0.75
                },
                interaction: { hover: true },
                groups: {
                    Task: { color: '#4e79a7' },
                    TaskDetail: { color: '#f28e2b' },
                    Comment: { color: '#e15759' },
                    User: { color: '#76b7b2' },
                    Requester: { color: '#b07aa1', shape: 'dot' },
                    Category: { color: '#59a14f' }
                }
            }
        );

        network.on('selectNode', params => {
            if (dotnetRef && params.nodes.length > 0) {
                dotnetRef.invokeMethodAsync('OnNodeSelected', params.nodes[0]);
            }
        });

        // Notify Blazor once physics stabilization finishes. We keep physics
        // enabled so nodes settle gently after a drag, but the higher
        // centralGravity + damping + minVelocity above ensure motion actually
        // stops instead of drifting forever.
        network.once('stabilizationIterationsDone', () => notifyRenderComplete());

        // Safety fallback in case stabilization event never fires (small
        // graphs or pathological layouts).
        setTimeout(notifyRenderComplete, 8000);
    }

    // Synthesized "Requester" nodes are now produced server-side in
    // HelpDeskGraphBuilder and are present in graph.json — no client-side
    // augmentation needed.

    function readPropCaseInsensitive(obj, ...names) {        if (!obj) return undefined;
        for (const n of names) {
            if (obj[n] !== undefined) return obj[n];
        }
        const lower = {};
        for (const k of Object.keys(obj)) lower[k.toLowerCase()] = obj[k];
        for (const n of names) {
            const v = lower[n.toLowerCase()];
            if (v !== undefined) return v;
        }
        return undefined;
    }

    function toArray(v) {
        if (!v) return [];
        if (Array.isArray(v)) return v;
        if (typeof v === 'object') return Object.values(v);
        return [];
    }

    function computeVisibility(doc, filter) {
        const typesArr = toArray(readPropCaseInsensitive(filter, 'nodeTypes', 'NodeTypes'));
        const statusArr = toArray(readPropCaseInsensitive(filter, 'taskStatuses', 'TaskStatuses'));
        const detailArr = toArray(readPropCaseInsensitive(filter, 'detailTypes', 'DetailTypes'));
        const userArr = toArray(readPropCaseInsensitive(filter, 'users', 'Users'));
        const taskArr = toArray(readPropCaseInsensitive(filter, 'tasks', 'Tasks'));
        const requesterArr = toArray(readPropCaseInsensitive(filter, 'requesters', 'Requesters'));

        const types = new Set(typesArr);
        const statuses = new Set(statusArr);
        const details = new Set(detailArr);
        const users = new Set(userArr);
        const tasks = new Set(taskArr);
        const requesters = new Set(requesterArr);

        const allTypes = types.size === 0;
        const allStatus = statuses.size === 0;
        const allDetail = details.size === 0;
        const allUsers = users.size === 0;
        const allTasks = tasks.size === 0;
        const allRequesters = requesters.size === 0;

        // True when the user has narrowed the view in any way that should
        // cause us to hide orphan Category / User / Requester nodes.
        const narrowing = !allStatus || !allDetail || !allUsers || !allTasks || !allRequesters
            || (!allTypes && types.size < new Set(doc.nodes.map(n => n.type)).size);

        // Pass 1 — direct rules on each node.
        const keep = new Set();
        for (const n of doc.nodes) {
            if (!allTypes && !types.has(n.type)) continue;

            if (n.type === 'User' && !allUsers && !users.has(n.id)) continue;
            if (n.type === 'Task' && !allTasks && !tasks.has(n.id)) continue;
            if (n.type === 'Requester' && !allRequesters && !requesters.has(n.id)) continue;

            if (n.type === 'Task' && !allStatus) {
                const s = n.data ? (n.data.status ?? n.data.Status) : null;
                if (!statuses.has(s)) continue;
            }
            if (n.type === 'TaskDetail' && !allDetail) {
                const d = n.data ? (n.data.detailType ?? n.data.DetailType) : null;
                if (!details.has(d)) continue;
            }
            keep.add(n.id);
        }

        // Pass 2 — task whose requester is not in the Requesters selection → drop task.
        // The Requesters facet contains BOTH registered users and synthesized
        // Requester hub nodes (anything that is a REQUESTED_BY target), so this
        // single check covers both kinds of requesters.
        if (!allRequesters) {
            for (const n of doc.nodes) {
                if (n.type !== 'Task' || !keep.has(n.id)) continue;

                // Find requester edge target for this task.
                let requesterTarget = null;
                for (const e of doc.edges) {
                    if (e.type !== 'REQUESTED_BY') continue;
                    if (e.source !== n.id) continue;
                    requesterTarget = e.target;
                    break;
                }

                // Only filter when the task HAS a requester edge that points
                // somewhere outside the selection. Tasks with no requester at
                // all are unaffected by the Requesters filter.
                if (requesterTarget && !requesters.has(requesterTarget)) {
                    keep.delete(n.id);
                }
            }
        }

        // Pass 3 — details/comments whose parent task is hidden → drop them.
        const taskOfDetail = new Map();
        for (const e of doc.edges) {
            if (e.source && e.target &&
                e.source.startsWith('task:') &&
                (e.target.startsWith('detail:') || e.target.startsWith('comment:'))) {
                taskOfDetail.set(e.target, e.source);
            }
        }
        for (const [detailId, taskId] of taskOfDetail) {
            if (!keep.has(taskId)) keep.delete(detailId);
        }

        if (narrowing) {
            // Pass 4a — hide User / Requester nodes that have no remaining
            // linked task (avoids floating hub nodes once tasks get filtered).
            const taskCountByNode = new Map();
            for (const e of doc.edges) {
                if (!e.source || !e.target) continue;
                if (!e.source.startsWith('task:')) continue;
                if (!keep.has(e.source)) continue;
                if (e.target.startsWith('user:') || e.target.startsWith('requester:')) {
                    taskCountByNode.set(e.target, (taskCountByNode.get(e.target) || 0) + 1);
                }
            }
            for (const n of doc.nodes) {
                if (n.type !== 'User' && n.type !== 'Requester') continue;
                if (!keep.has(n.id)) continue;
                if (!taskCountByNode.has(n.id)) keep.delete(n.id);
            }

            // Pass 4b — hide Category nodes that contain no remaining task
            // (directly or through their descendant categories).
            const catHasTask = new Set();
            for (const e of doc.edges) {
                if (e.type !== 'IN_CATEGORY') continue;
                if (!keep.has(e.source)) continue; // source = task
                if (typeof e.target !== 'string' || !e.target.startsWith('category:')) continue;
                catHasTask.add(e.target);
            }
            // Propagate up the CHILD_OF chain so ancestor categories of
            // populated children remain visible.
            const parentOf = new Map();
            for (const e of doc.edges) {
                if (e.type !== 'CHILD_OF') continue;
                if (typeof e.source === 'string' && typeof e.target === 'string'
                    && e.source.startsWith('category:') && e.target.startsWith('category:')) {
                    parentOf.set(e.source, e.target);
                }
            }
            const reachable = new Set(catHasTask);
            for (const start of catHasTask) {
                let cur = parentOf.get(start);
                while (cur && !reachable.has(cur)) {
                    reachable.add(cur);
                    cur = parentOf.get(cur);
                }
            }
            for (const n of doc.nodes) {
                if (n.type !== 'Category') continue;
                if (!keep.has(n.id)) continue;
                if (!reachable.has(n.id)) keep.delete(n.id);
            }
        }

        return keep;
    }

    function applyFilters(filter) {
        if (!lastDoc || !network) return;
        const keep = computeVisibility(lastDoc, filter);

        network.body.data.nodes.update(
            lastDoc.nodes.map(n => ({ id: n.id, hidden: !keep.has(n.id) }))
        );
        network.body.data.edges.update(
            lastDoc.edges.map(e => ({
                id: e.id,
                hidden: !(keep.has(e.source) && keep.has(e.target))
            }))
        );
    }

    function unselectAll() {
        if (network) network.unselectAll();
    }

    function getDocument() {
        return lastDoc;
    }

    return { init, render, applyFilters, unselectAll, getDocument };
})();
