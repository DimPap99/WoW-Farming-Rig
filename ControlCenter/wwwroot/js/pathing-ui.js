// Small page helpers for the Control Center: on-demand scripts, the toast and the rail's
// remembered state. Loaded once by App.razor; everything hangs off window.pathingUi.

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

    let toastTimer = 0;

    /** Shows a short message in the toast; also the target of the watch hub's "log" calls. */
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

    window.pathingUi = { loadScript, log, storageGet, storageSet, relayout };

    // The shared map scripts call a global log().
    if (typeof window.log !== 'function') {
        window.log = log;
    }
})();
