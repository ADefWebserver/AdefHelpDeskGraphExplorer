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
            title: JSON.stringify(n.data, null, 2),
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
                physics: { stabilization: true },
                interaction: { hover: true },
                groups: {
                    Task: { color: '#4e79a7' },
                    TaskDetail: { color: '#f28e2b' },
                    Comment: { color: '#e15759' },
                    User: { color: '#76b7b2' },
                    Category: { color: '#59a14f' }
                }
            }
        );

        network.on('selectNode', params => {
            if (dotnetRef && params.nodes.length > 0) {
                dotnetRef.invokeMethodAsync('OnNodeSelected', params.nodes[0]);
            }
        });

        // Notify Blazor once physics stabilization finishes.
        network.once('stabilizationIterationsDone', () => notifyRenderComplete());

        // Safety fallback in case stabilization event never fires (small graphs).
        setTimeout(notifyRenderComplete, 5000);
    }

    function readPropCaseInsensitive(obj, ...names) {
        if (!obj) return undefined;
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

        const types = new Set(typesArr);
        const statuses = new Set(statusArr);
        const details = new Set(detailArr);
        const users = new Set(userArr);
        const tasks = new Set(taskArr);

        const allTypes = types.size === 0;
        const allStatus = statuses.size === 0;
        const allDetail = details.size === 0;
        const allUsers = users.size === 0;
        const allTasks = tasks.size === 0;

        // Pass 1 — direct rules on each node.
        const keep = new Set();
        for (const n of doc.nodes) {
            if (!allTypes && !types.has(n.type)) continue;

            if (n.type === 'User' && !allUsers && !users.has(n.id)) continue;
            if (n.type === 'Task' && !allTasks && !tasks.has(n.id)) continue;

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

        // Pass 2 — task whose requester user is hidden → drop task.
        if (!allUsers) {
            for (const n of doc.nodes) {
                if (n.type !== 'Task' || !keep.has(n.id)) continue;
                const requester = n.data && (n.data.requesterUserId ?? n.data.RequesterUserId);
                if (requester !== undefined && requester !== null) {
                    const userId = `user:${requester}`;
                    if (!users.has(userId)) keep.delete(n.id);
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
