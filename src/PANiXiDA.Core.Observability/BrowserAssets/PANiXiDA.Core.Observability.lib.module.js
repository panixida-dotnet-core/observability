import { onCLS, onFCP, onINP, onLCP, onTTFB } from './web-vitals.min.js';

let receiver;
let initialized = false;
let wasmStarted;
let runtimeReady;
let resources;
let routeReady = false;
let currentNavigation = 0;
const entries = new Map();
const pending = [];
const maximumResources = 2048;

function send(method, ...args) {
    if (receiver && routeReady) {
        receiver.invokeMethodAsync(method, ...args).catch(() => {});
    } else if ((!initialized || !routeReady) && pending.length < 16) {
        pending.push([method, args]);
    }
}

function captureResources(batch) {
    for (const entry of batch) {
        const url = new URL(entry.name, document.baseURI);
        if (url.pathname.includes('/_framework/') && entries.size < maximumResources) {
            entries.set(`${entry.name}:${entry.startTime}`, entry);
        }
    }
}

function beforeWasm() {
    if (wasmStarted !== undefined) {
        return;
    }
    wasmStarted = performance.now();
    captureResources(performance.getEntriesByType('resource'));
    if (globalThis.PerformanceObserver?.supportedEntryTypes?.includes('resource')) {
        resources = new PerformanceObserver(list => captureResources(list.getEntries()));
        resources.observe({ type: 'resource', buffered: true });
    }
}

function resourceKind(name) {
    const path = new URL(name, document.baseURI).pathname;
    if (path.endsWith('.wasm')) return 'wasm';
    if (/\.(dll|dat)$/.test(path)) return 'assembly';
    if (path.endsWith('.js')) return 'javascript';
    if (path.endsWith('.json')) return 'configuration';
    return 'other';
}

function afterWasm() {
    if (runtimeReady !== undefined || wasmStarted === undefined) {
        return;
    }
    runtimeReady = performance.now();
    captureResources(resources?.takeRecords() ?? []);
    captureResources(performance.getEntriesByType('resource'));
    resources?.disconnect();
    send('RecordStartup', 'runtime', wasmStarted, runtimeReady - wasmStarted, performance.timeOrigin);
    send('RecordStartup', 'document_to_runtime', 0, runtimeReady, performance.timeOrigin);

    const groups = new Map();
    for (const entry of entries.values()) {
        if (entry.responseEnd > runtimeReady) continue;
        const kind = resourceKind(entry.name);
        const group = groups.get(kind) ?? [0, 0, 0, 0];
        group[0]++;
        group[1] += entry.duration;
        group[2] += entry.transferSize ?? 0;
        group[3] += entry.decodedBodySize ?? 0;
        groups.set(kind, group);
    }
    for (const [kind, values] of groups) {
        send('RecordResources', kind, ...values);
    }
    entries.clear();

    requestAnimationFrame(() => requestAnimationFrame(timestamp => {
        send('RecordStartup', 'first_frame', runtimeReady, timestamp - runtimeReady, performance.timeOrigin);
    }));
}

globalThis.panixidaObservability = {
    start(target) {
        receiver = target;
        if (routeReady) {
            for (const [method, args] of pending.splice(0)) send(method, ...args);
        }
        if (initialized) return;
        initialized = true;
        const report = metric => {
            send('RecordVital', metric.name, metric.value, metric.rating, metric.navigationType);
        };
        const options = { reportAllChanges: true };
        onCLS(report, options);
        onFCP(report, options);
        onINP(report, options);
        onLCP(report, options);
        onTTFB(report, options);
    },
    navigationStarted(id) {
        currentNavigation = id;
    },
    routeChanged(id) {
        currentNavigation = id;
        routeReady = true;
        for (const [method, args] of pending.splice(0)) send(method, ...args);
        const matched = performance.now();
        requestAnimationFrame(() => requestAnimationFrame(timestamp => {
            if (currentNavigation === id) send('RecordNavigation', id, timestamp - matched);
        }));
    },
    stop() {
        currentNavigation++;
        receiver = undefined;
        resources?.disconnect();
        entries.clear();
        pending.length = 0;
    }
};

export function beforeStart() { beforeWasm(); }
export function beforeWebAssemblyStart() { beforeWasm(); }
export function afterStarted() { afterWasm(); }
export function afterWebAssemblyStarted() { afterWasm(); }
