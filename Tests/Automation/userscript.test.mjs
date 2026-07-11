import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';

const repoRoot = path.resolve(import.meta.dirname, '..', '..');
const scriptPath = path.join(repoRoot, 'Scripts', '快递助手订单推送.user.js');
const source = fs.readFileSync(scriptPath, 'utf8');

function between(startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start);
  assert.ok(start >= 0 && end > start, `Cannot extract ${startMarker}`);
  return source.slice(start, end);
}

function createLeaseWorker(store, token, closed) {
  const context = {
    REFUND_WORKER_HEARTBEAT_KEY: 'heartbeat',
    REFUND_WORKER_TOKEN: token,
    REFUND_WORKER_STALE_MS: 10 * 60 * 1000,
    GM_getValue: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    GM_setValue: (key, value) => store.set(key, value),
    delay: milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)),
    document: { title: '' },
    window: { close: () => closed.push(token) },
    location: { replace: () => {} },
    setTimeout,
    Math,
    Date
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function getRefundWorkerHeartbeat()', '    function getApiUrl()') +
      ';globalThis.claimLease=claimRefundWorkerLease;',
    context);
  return context;
}

test('concurrent refund workers keep exactly one lease owner', async () => {
  const store = new Map();
  const closed = [];
  const workers = Array.from({ length: 8 }, (_, index) => createLeaseWorker(store, `worker-${index}`, closed));
  const results = await Promise.all(workers.map(worker => worker.claimLease()));
  assert.equal(results.filter(Boolean).length, 1);
  assert.equal(closed.length, 7);
});

test('fresh lease is retained and stale lease can recover', async () => {
  const store = new Map([['heartbeat', { token: 'owner', time: Date.now() }]]);
  const closed = [];
  assert.equal(await createLeaseWorker(store, 'new-worker', closed).claimLease(), false);
  assert.deepEqual(store.get('heartbeat').token, 'owner');

  store.set('heartbeat', { token: 'stale-owner', time: Date.now() - 10 * 60 * 1000 - 1 });
  assert.equal(await createLeaseWorker(store, 'replacement', closed).claimLease(), true);
  assert.equal(store.get('heartbeat').token, 'replacement');
});

function createDiscoveryContext(initialStore, reachableHosts) {
  const store = new Map(Object.entries(initialStore));
  const document = {
    createElement: () => ({ style: {}, remove() {} }),
    body: { appendChild() {} }
  };
  const context = {
    DEFAULT_HOST: '127.0.0.1', DEFAULT_PORT: 5280, DEFAULT_ADDRESS: '127.0.0.1:5280',
    DISCOVERY_DONE_KEY: 'monitor_auto_discovery_done', DISCOVERY_TIMEOUT: 1, DISCOVERY_BATCH_SIZE: 32,
    GM_getValue: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    GM_setValue: (key, value) => store.set(key, value),
    getStorageUrl: (host, port) => `http://${host}:${port}/api/storage`,
    GM_xmlhttpRequest: options => {
      const host = new URL(options.url).hostname;
      queueMicrotask(() => options.onload({ status: reachableHosts.has(host) ? 200 : 503 }));
    },
    window: {}, document, setTimeout, clearTimeout, URL, Promise, Set, Array, String, Number, Boolean, Math
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function getBaseUrl(', '    let monitorDiscoveryPromise = null;') +
      ';getWebRtcLocalPrefixes=async()=>["192.168.31"];globalThis.findMonitor=findMonitorAddress;',
    context);
  return { context, store };
}

test('automatic discovery never switches away from an offline saved monitor', async () => {
  const { context, store } = createDiscoveryContext(
    { monitor_address: '192.168.31.250:5280' },
    new Set(['192.168.31.10']));
  assert.equal(await context.findMonitor(false), '');
  assert.equal(store.get('monitor_address'), '192.168.31.250:5280');
});

test('first run discovery deterministically selects the first reachable monitor', async () => {
  const { context, store } = createDiscoveryContext({}, new Set(['192.168.31.2', '192.168.31.3']));
  assert.equal(await context.findMonitor(false), '192.168.31.2:5280');
  assert.equal(store.get('monitor_address'), '192.168.31.2:5280');
});
