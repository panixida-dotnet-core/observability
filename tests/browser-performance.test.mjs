import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const filename = new URL('../src/PANiXiDA.Core.Observability/BrowserAssets/PANiXiDA.Core.Observability.lib.module.js', import.meta.url);
const source = await readFile(filename, 'utf8');

function setup({ observer = true, beforeConnection = false, reject = false } = {}) {
    let now = 100;
    const calls = [];
    const callbacks = {};
    const frames = [];
    const batches = [];
    const makeVital = name => (callback, options) => {
        assert.equal(options.reportAllChanges, true);
        callbacks[name] = callback;
    };
    class Observer {
        static supportedEntryTypes = ['resource'];
        constructor(callback) { this.callback = callback; batches.push(this); }
        observe(options) { assert.equal(options.buffered, true); }
        takeRecords() { return [resource('late.wasm', 10)]; }
        disconnect() { this.disconnected = true; }
    }
    const resource = (name, startTime = 0) => ({
        name: `https://admin.test/_framework/${name}?ignored=secret`,
        startTime, duration: 20, responseEnd: startTime + 20,
        transferSize: 100, decodedBodySize: 200
    });
    const timings = [resource('runtime.wasm'), resource('module.dll'), resource('timezone.dat'),
        resource('runtime.js'), resource('boot.json'), { ...resource('symbols.bin'), transferSize: undefined, decodedBodySize: undefined },
        resource('future.wasm', 1000), { ...resource('ignored.js'), name: 'https://admin.test/app.js' }];
    const context = vm.createContext({
        URL, Map, performance: { now: () => now, timeOrigin: 1700000000000, getEntriesByType: () => timings },
        document: { baseURI: 'https://admin.test/' },
        requestAnimationFrame: callback => frames.push(callback),
        PerformanceObserver: observer ? Observer : undefined,
        onCLS: makeVital('CLS'), onFCP: makeVital('FCP'), onINP: makeVital('INP'),
        onLCP: makeVital('LCP'), onTTFB: makeVital('TTFB')
    });
    const body = source.replace(/^import .*\n/, line => ' '.repeat(line.length - 1) + '\n')
        .replaceAll('export function ', '       function ');
    vm.runInContext(body, context, { filename: fileURLToPath(filename) });
    context.hooks = vm.runInContext('({ beforeStart, beforeWebAssemblyStart, afterStarted, afterWebAssemblyStarted })', context);
    const receiver = { invokeMethodAsync(method, ...args) {
        calls.push([method, ...args]);
        return reject ? Promise.reject(new Error('disposed')) : Promise.resolve();
    } };
    context.hooks.afterStarted();
    context.hooks.beforeStart();
    context.hooks.beforeWebAssemblyStart();
    batches[0]?.callback({ getEntries: () => [resource('runtime.wasm'), resource('another.js')] });
    if (!beforeConnection) context.panixidaObservability.start(receiver);
    now = 400;
    context.hooks.afterWebAssemblyStarted();
    context.hooks.afterStarted();
    frames.shift()();
    frames.shift()(432);
    if (beforeConnection) context.panixidaObservability.start(receiver);
    context.panixidaObservability.start(receiver);
    return { calls, callbacks, context, resource, timings, batches };
}

test('startup phases and resource groups retain timings and exclude URLs and future resources', () => {
    const { calls, callbacks, context, batches } = setup();
    assert.deepEqual(calls.filter(call => call[0] === 'RecordStartup'), [
        ['RecordStartup', 'runtime', 100, 300, 1700000000000],
        ['RecordStartup', 'document_to_runtime', 0, 400, 1700000000000],
        ['RecordStartup', 'first_frame', 400, 32, 1700000000000]
    ]);
    const resources = calls.filter(call => call[0] === 'RecordResources');
    assert.equal(resources.length, 5);
    assert.deepEqual(resources.find(call => call[1] === 'wasm'), ['RecordResources', 'wasm', 2, 40, 200, 400]);
    assert.deepEqual(resources.find(call => call[1] === 'assembly'), ['RecordResources', 'assembly', 2, 40, 200, 400]);
    assert.equal(JSON.stringify(calls).includes('secret'), false);
    assert.equal(batches[0].disconnected, true);
    for (const name of ['CLS', 'FCP', 'INP', 'LCP', 'TTFB']) {
        callbacks[name]({ name, value: 123, rating: 'good', navigationType: 'navigate' });
    }
    assert.equal(calls.filter(call => call[0] === 'RecordVital').length, 5);
    context.panixidaObservability.stop();
    callbacks.CLS({ name: 'CLS', value: 0.1 });
    assert.equal(calls.filter(call => call[0] === 'RecordVital').length, 5);
});

test('startup is buffered until the managed providers connect and works without PerformanceObserver', () => {
    const { calls, context } = setup({ observer: false, beforeConnection: true });
    assert.equal(calls.filter(call => call[0] === 'RecordStartup').length, 3);
    context.panixidaObservability.stop();
});

test('disposed managed receivers do not create unhandled promise rejections', async () => {
    const { callbacks, context } = setup({ reject: true });
    callbacks.LCP({ name: 'LCP', value: 1, rating: 'good', navigationType: 'navigate' });
    await new Promise(resolve => setImmediate(resolve));
    context.panixidaObservability.stop();
});
