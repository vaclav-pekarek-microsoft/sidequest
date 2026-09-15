// Parent maps this public source to /service-worker.js for root scope.
const cacheName = "sidequest-public-experience-v1";
const fallback = "/experience/offline.html";
const assets = [
    fallback, "/experience/offline.css?v=1", "/experience/offline.js?v=1",
    "/experience/snapshot-store.js?v=1", "/experience/refresh.js?v=1", "/experience/manifest.webmanifest?v=1",
    "/experience/icon.svg?v=1", "/experience/icon-192.png?v=1", "/experience/icon-512.png?v=1"
];
const allowed = new Set(assets.map(path => new URL(path, self.location.origin).href));
self.addEventListener("install", event => {
    event.waitUntil((async () => {
        const cache = await caches.open(cacheName);
        for (const path of assets) {
            const response = await fetch(path, { credentials: "omit", cache: "reload", redirect: "error" });
            if (!response.ok || response.redirected) throw new Error("Public offline assets are unavailable.");
            await cache.put(path, response);
        }
        await self.skipWaiting();
    })());
});
self.addEventListener("activate", event => {
    event.waitUntil((async () => {
        for (const name of await caches.keys())
            if (name.startsWith("sidequest-public-experience-") && name !== cacheName) await caches.delete(name);
        await self.clients.claim();
    })());
});
self.addEventListener("fetch", event => {
    const url = new URL(event.request.url);
    if (event.request.method !== "GET" || url.origin !== self.location.origin) return;
    // Hosting layers differ in percent-decoding. Encoded paths stay network-only rather than risk a protected-download fallback.
    const path = url.pathname.replace(/\/+/g, "/").toLowerCase();
    if (path.includes("%") || path === "/media" || path.startsWith("/media/") ||
        path === "/notifications/calendar" || path.startsWith("/notifications/calendar/")) return;
    if (event.request.mode === "navigate") {
        event.respondWith((async () => {
            try { return await fetch(event.request); }
            catch { return (await caches.open(cacheName)).match(fallback); }
        })());
    } else if (allowed.has(event.request.url)) {
        event.respondWith((async () => (await (await caches.open(cacheName)).match(event.request)) ??
            fetch(event.request))());
    }
    // API responses, authenticated HTML, images and arbitrary responses are never cached.
});
