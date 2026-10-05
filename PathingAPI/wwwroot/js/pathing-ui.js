// Small page helpers for PathingAPI: on-demand scripts, API calls and the toast.
// Loaded once by _Host.cshtml; everything hangs off window.pathingUi.

(function () {
    'use strict';

    const loading = new Map();

    /** Loads a script once and resolves when it has run. */
    function loadScript(src) {
        if (!loading.has(src)) {
            loading.set(src, new Promise((resolve, reject) => {
                const tag = document.createElement('script');
                tag.src = src;
                tag.async = true;
                tag.onload = () => resolve();
                tag.onerror = () => {
                    loading.delete(src);
                    reject(new Error('Could not load ' + src));
                };
                document.head.appendChild(tag);
            }));
        }
        return loading.get(src);
    }

    /**
     * Calls an API route the way the bot does and reports what came back:
     * { status, points, ms }. A 429 means another search held the gate.
     */
    async function callApi(url) {
        const started = performance.now();
        try {
            const response = await fetch(url, { cache: 'no-store' });
            let points = 0;
            if (response.ok && (response.headers.get('content-type') || '').includes('json')) {
                const body = await response.json();
                points = Array.isArray(body) ? body.length : 0;
            }
            return { status: response.status, points: points, ms: Math.round(performance.now() - started) };
        } catch (e) {
            return { status: 0, points: 0, ms: Math.round(performance.now() - started) };
        }
    }

    let toastTimer = 0;

    /** Shows a short message in the toast; also the target of the server's "log" calls. */
    function log(message) {
        console.log(message);
        const toast = document.getElementById('pa-toast');
        if (!toast) return;
        toast.textContent = message;
        toast.classList.add('is-visible');
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => toast.classList.remove('is-visible'), 4000);
    }

    function storageGet(key) {
        try { return localStorage.getItem(key); } catch (_) { return null; }
    }

    function storageSet(key, value) {
        try { localStorage.setItem(key, value); } catch (_) { /* private mode */ }
    }

    /** After the stage changes size without the window doing so (rail collapse). */
    function relayout() {
        requestAnimationFrame(() => window.dispatchEvent(new Event('resize')));
    }

    window.pathingUi = { loadScript, callApi, log, storageGet, storageSet, relayout };

    // Server code and the shared map scripts call a global log().
    if (typeof window.log !== 'function') {
        window.log = log;
    }
})();
